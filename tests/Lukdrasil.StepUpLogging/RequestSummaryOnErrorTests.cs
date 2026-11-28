using System.Net;
using System.Collections.Generic;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Pins <see cref="StepUpLoggingOptions.RequestSummaryOnError"/> through the real pipeline: the events are those the
/// output loggers export, captured by a config-declared sink.
/// </summary>
public class RequestSummaryOnErrorTests
{
    private const string UserAgent = "curl/8.4.0";

    private static Task<LogEvent[]> ExportedAsync(string path, params (string Key, string Value)[] options)
        => ExportedAsync(path, steppedUp: false, configure: null, options);

    private static Task<LogEvent[]> ExportedAsync(string path, bool steppedUp, params (string Key, string Value)[] options)
        => ExportedAsync(path, steppedUp, configure: null, options);

    private static async Task<LogEvent[]> ExportedAsync(string path, bool steppedUp, Action<IServiceProvider, LoggerConfiguration>? configure, params (string Key, string Value)[] options)
    {
        var captureKey = Guid.NewGuid().ToString("N");
        var settings = new Dictionary<string, string?>
        {
            ["SerilogStepUp:EnableOtlpExporter"] = "false",
            ["SerilogStepUp:AlwaysLogRequestSummary"] = "true",
            ["Serilog:Using:0"] = typeof(KeyedCaptureSink).Assembly.GetName().Name,
            ["Serilog:WriteTo:0:Name"] = nameof(KeyedCaptureSink.StepUpCapture),
            ["Serilog:WriteTo:0:Args:key"] = captureKey,
        };
        foreach (var (key, value) in options)
        {
            settings["SerilogStepUp:" + key] = value;
        }

        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(settings);
            if (configure is null)
            {
                builder.AddStepUpLogging();
            }
            else
            {
                builder.AddStepUpLogging(configure);
            }

            await using (var app = builder.Build())
            {
                app.Use((context, next) =>
                {
                    context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("azp", "rate-limited-client")], "Bearer"));
                    context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
                    context.Connection.RemotePort = 51234;
                    return next();
                });
                app.UseStepUpRequestLogging();
                app.MapGet("/status/{code:int}", (int code) => Results.StatusCode(code));
                app.MapGet("/throw", IResult () => throw new InvalidOperationException("boom"));
                app.MapGet("/abort/{code:int}", (int code, HttpContext context) =>
                {
                    context.Response.StatusCode = code;
                    context.Abort();
                });

                await app.StartAsync(TestContext.Current.CancellationToken);
                if (steppedUp)
                {
                    app.Services.GetRequiredService<StepUpLoggingController>().Trigger();
                }

                using var client = app.GetTestClient();
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                request.Headers.Add("X-Forwarded-For", "198.51.100.9");
                try
                {
                    using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
                }
                catch (Exception)
                {
                    // The test server rethrows the handler's exception and fails an aborted request.
                }

                await Task.Delay(200, TestContext.Current.CancellationToken);
                await app.StopAsync(TestContext.Current.CancellationToken);
            }

            return [.. KeyedCaptureSink.Events(captureKey).Where(e => IsSummary(e) || IsCompletion(e))];
        }
        finally
        {
            KeyedCaptureSink.Forget(captureKey);
        }
    }

    private static bool IsSummary(LogEvent e) => LogProperties.HasFlag(e, LogProperties.IsRequestSummary);

    private static bool IsCompletion(LogEvent e) => e.Properties.ContainsKey("RequestMethod");

    private static object? Scalar(LogEvent e, string name) => e.Properties.TryGetValue(name, out var value) ? Assert.IsType<ScalarValue>(value).Value : null;

    [Theory]
    [InlineData(null)]
    [InlineData("Keep")]
    public async Task Keep_AFailedRequest_WritesTheSummaryAndTheCompletionEvent(string? mode)
    {
        var events = await ExportedAsync("/status/429", mode is null ? [] : [("RequestSummaryOnError", mode)]);

        Assert.Single(events, IsSummary);
        Assert.Single(events, IsCompletion);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Omit_AFailedRequest_WritesOnlyTheCompletionEvent(int status)
    {
        var events = await ExportedAsync($"/status/{status}", ("RequestSummaryOnError", "Omit"));

        var completion = Assert.Single(events);
        Assert.True(IsCompletion(completion));
        Assert.Equal(status, Scalar(completion, "StatusCode"));
    }

    [Fact]
    public async Task Omit_TheCompletionEvent_CarriesTheSummaryOnlyFields()
    {
        var events = await ExportedAsync("/status/429", ("RequestSummaryOnError", "Omit"));

        var completion = Assert.Single(events);
        Assert.Equal(nameof(ClientKind.Tool), Scalar(completion, "ClientKind"));
        Assert.Equal(UserAgent, Scalar(completion, "UserAgent"));
        Assert.Equal("rate-limited-client", Scalar(completion, "ClientId"));
        Assert.Equal("/status/{code:int}", Scalar(completion, "Route"));
        Assert.Equal(true, Scalar(completion, "IsAuthenticated"));
        Assert.Equal("Bearer", Scalar(completion, "AuthScheme"));
    }

    [Fact]
    public async Task Omit_Compact_CarriesOnlyTheCompactFields()
    {
        var events = await ExportedAsync("/status/429", ("RequestSummaryOnError", "Omit"), ("RequestSummaryDetail", "Compact"));

        var completion = Assert.Single(events);
        Assert.Equal("rate-limited-client", Scalar(completion, "ClientId"));
        Assert.False(completion.Properties.ContainsKey("AuthScheme"));
    }

    [Fact]
    public async Task Omit_ASuccessfulRequest_StillWritesTheSummary_EvenWhenTheCompletionEventIsExported()
    {
        var events = await ExportedAsync("/status/200", ("RequestSummaryOnError", "Omit"), ("BaseLevel", "Information"));

        Assert.Single(events, IsSummary);
        var completion = Assert.Single(events, IsCompletion);
        Assert.False(completion.Properties.ContainsKey("ClientKind"));
    }

    [Fact]
    public async Task Omit_BaseLevelAboveTheCompletionLevel_KeepsTheSummary()
    {
        var events = await ExportedAsync("/status/429", ("RequestSummaryOnError", "Omit"), ("BaseLevel", "Error"));

        var summary = Assert.Single(events);
        Assert.True(IsSummary(summary));
    }

    [Fact]
    public async Task Omit_AnUnhandledException_WritesOnlyTheErrorCompletionEvent()
    {
        var events = await ExportedAsync("/throw", ("RequestSummaryOnError", "Omit"));

        var completion = Assert.Single(events);
        Assert.True(IsCompletion(completion));
        Assert.Equal(LogEventLevel.Error, completion.Level);
        Assert.Equal(UserAgent, Scalar(completion, "UserAgent"));
    }

    [Fact]
    public async Task Omit_AClientErrorRequest_CarriesTheFullClientIdentityOnTheCompletionEvent()
    {
        var events = await ExportedAsync("/status/429", ("RequestSummaryOnError", "Omit"));

        var completion = Assert.Single(events);
        Assert.Equal("203.0.113.7", Scalar(completion, "ClientIp"));
        Assert.Equal("198.51.100.9", Scalar(completion, "ForwardedFor"));
        Assert.Equal(51234, Scalar(completion, "RemotePort"));
    }

    [Fact]
    public async Task AnUnhandledException_IsNotAClientErrorRequest()
    {
        var events = await ExportedAsync("/throw");

        Assert.Equal(2, events.Length);
        Assert.All(events, e => Assert.Null(Scalar(e, "ClientIp")));
    }

    [Fact]
    public async Task TheCompletionEvent_HasTheSourceContextTheReceiptRecognizes()
    {
        var events = await ExportedAsync("/status/429");

        Assert.Equal(CompletionReceipt.SourceContext, Scalar(Assert.Single(events, IsCompletion), "SourceContext"));
    }

    [Fact]
    public async Task Omit_SteppedUpAboveTheBaseLevel_WritesOnlyTheCompletionEvent()
    {
        var events = await ExportedAsync("/status/429", steppedUp: true, ("RequestSummaryOnError", "Omit"), ("BaseLevel", "Error"));

        var completion = Assert.Single(events);
        Assert.True(IsCompletion(completion));
    }

    [Fact]
    public async Task Omit_ACompletionEventARootFilterDrops_KeepsTheSummary()
    {
        var events = await ExportedAsync("/status/429", steppedUp: false,
            (_, configuration) => configuration.Filter.ByExcluding(e => e.Properties.ContainsKey("RequestMethod")),
            ("RequestSummaryOnError", "Omit"));

        Assert.True(IsSummary(Assert.Single(events)));
    }

    [Fact]
    public async Task Omit_ACompletionEventExportedThroughAlwaysExport_WritesOnlyTheCompletionEvent()
    {
        var events = await ExportedAsync("/status/429", ("RequestSummaryOnError", "Omit"), ("BaseLevel", "Error"), ("AlwaysExportCategories:0", CompletionReceipt.SourceContext));

        var completion = Assert.Single(events);
        Assert.True(IsCompletion(completion));
        Assert.Equal(UserAgent, Scalar(completion, "UserAgent"));
    }

    [Fact]
    public async Task Omit_TheCarriedCompletionEvent_IsNotMarkedImmediate()
    {
        var events = await ExportedAsync("/status/429", ("RequestSummaryOnError", "Omit"));

        Assert.False(LogProperties.HasFlag(Assert.Single(events), LogProperties.IsImmediate));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    public async Task Omit_AnAbortedRequest_WhoseInformationCompletionIsNotExported_KeepsTheSummary(int status)
    {
        var events = await ExportedAsync($"/abort/{status}", ("RequestSummaryOnError", "Omit"));

        Assert.True(IsSummary(Assert.Single(events)));
    }

    [Fact]
    public async Task Omit_AnAbortedFailedRequest_WhoseCompletionIsExported_WritesOnlyTheCompletionEvent()
    {
        var events = await ExportedAsync("/abort/400", ("RequestSummaryOnError", "Omit"), ("BaseLevel", "Information"));

        var completion = Assert.Single(events);
        Assert.Equal(LogEventLevel.Information, completion.Level);
        Assert.Equal(true, Scalar(completion, "Aborted"));
    }

    [Fact]
    public async Task Omit_AnAbortedSuccessfulRequest_WhoseCompletionIsExported_KeepsTheSummary()
    {
        var events = await ExportedAsync("/abort/200", ("RequestSummaryOnError", "Omit"), ("BaseLevel", "Information"));

        Assert.Single(events, IsSummary);
        Assert.Single(events, IsCompletion);
    }

    [Fact]
    public async Task Omit_TheCarriedFields_AreRedacted()
    {
        var events = await ExportedAsync("/status/429", ("RequestSummaryOnError", "Omit"), ("RedactionRegexes:0", "curl/[0-9.]+"), ("RedactionRegexes:1", "rate-limited"));

        var completion = Assert.Single(events);
        Assert.Equal("[REDACTED]", Scalar(completion, "UserAgent"));
        Assert.Equal("[REDACTED]-client", Scalar(completion, "ClientId"));
    }

    [Fact]
    public async Task Omit_Minimal_CarriesTheFieldsUnderTheSummaryShortKeys()
    {
        var events = await ExportedAsync("/status/429", ("RequestSummaryOnError", "Omit"), ("RequestSummaryDetail", "Minimal"));

        var completion = Assert.Single(events);
        Assert.Equal(nameof(ClientKind.Tool), Scalar(completion, "ck"));
        Assert.Equal(UserAgent, Scalar(completion, "ua"));
        Assert.Equal("rate-limited-client", Scalar(completion, "cid"));
        Assert.False(completion.Properties.ContainsKey("ClientKind"));
        Assert.False(completion.Properties.ContainsKey("ip"));
    }
}
