using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Hosting;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Pins issue #30: a <c>RedactionRegexes</c> pattern that matches the request path redacts the summary
/// <c>Path</c>, the "HTTP ..." <c>RequestPath</c>, the <c>LogRequest</c> span's <c>http.target</c> and every
/// route value carried inside the redacted part of the path, through the real middleware pipeline and through
/// the shared <c>RequestPathRedaction.RedactRouteValues</c> helper.
/// </summary>
public class RequestPathRedactionTests
{
    private const string ActionLinksPattern = "(?i)/Public/ActionLinks/[^/?]+";
    private const string ActionLinksPath = "/api/v2/Public/ActionLinks/abc123";
    private const string RedactedActionLinksPath = "/api/v2[REDACTED]";
    private const string Token = "abc123";

    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private sealed record Captured(
        HttpResponseMessage Response,
        List<LogEvent> Summaries,
        List<LogEvent> HttpEvents,
        List<Activity> LogRequestSpans,
        List<Activity> RedactionSpans);

    private static CompiledRedactionPatterns Patterns(params string[] patterns) =>
        new(patterns
            .Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
            .ToArray());

    private static CompiledRedactionPatterns TimingOutPatterns() =>
        new(new[] { new Regex("(a+)+$", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(1)) });

    private static async Task<IHost> BuildServerAsync(Serilog.ILogger logger, CompiledRedactionPatterns patterns, string[]? excludePaths)
    {
        var opts = new StepUpLoggingOptions
        {
            AlwaysLogRequestSummary = true,
            EnableActivityInstrumentation = true,
        };
        if (excludePaths is not null)
        {
            opts.ExcludePaths = excludePaths;
        }

        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton(Options.Create(opts));
                    services.AddSingleton(logger);
                    services.AddSingleton(sp => new StepUpLoggingController(opts, logger));
                    services.AddSingleton(patterns);
                    services.AddSingleton(new DiagnosticContext(logger));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseStepUpRequestLogging();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/api/{id}", async ctx =>
                        {
                            ctx.Response.StatusCode = 200;
                            await ctx.Response.WriteAsync("ok");
                        });
                        endpoints.MapGet("api/v2/{controller}/{action}/{token}", async ctx =>
                        {
                            ctx.Response.StatusCode = 200;
                            await ctx.Response.WriteAsync("ok");
                        });
                    });
                }))
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task<Captured> SendAsync(string path, CompiledRedactionPatterns patterns, string[]? excludePaths = null)
    {
        var capture = new CaptureSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();
        var previous = Log.Logger;
        Log.Logger = logger;
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == StepUpLoggingExtensions.RequestLoggingActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                lock (spans) spans.Add(a);
            }
        };
        ActivitySource.AddActivityListener(listener);
        try
        {
            using var host = await BuildServerAsync(logger, patterns, excludePaths);
            using var client = host.GetTestClient();

            var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            await Task.Delay(50, TestContext.Current.CancellationToken);

            List<Activity> stopped;
            lock (spans) stopped = spans.ToList();
            return new Captured(
                response,
                capture.Events.Where(e => e.Properties.TryGetValue(LogProperties.IsRequestSummary, out var v) && v is ScalarValue { Value: true }).ToList(),
                capture.Events.Where(e => e.MessageTemplate.Text.StartsWith("HTTP ", StringComparison.Ordinal)).ToList(),
                stopped.Where(a => a.OperationName == "LogRequest").ToList(),
                stopped.Where(a => a.OperationName == "ApplyRedaction").ToList());
        }
        finally
        {
            Log.Logger = previous;
            logger.Dispose();
        }
    }

    private static string? Scalar(LogEvent evt, string property) =>
        evt.Properties.TryGetValue(property, out var value) ? (value as ScalarValue)?.Value?.ToString() : null;

    private static string? RouteValue(LogEvent evt, string key)
    {
        Assert.True(evt.Properties.TryGetValue("RouteParameters", out var value), "event carries no RouteParameters");
        var dictionary = Assert.IsType<DictionaryValue>(value);
        var entry = dictionary.Elements.Single(kv => Equals(kv.Key.Value, key));
        return (entry.Value as ScalarValue)?.Value?.ToString();
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    private static IReadOnlyDictionary<string, object?> RedactRouteValues(
        IEnumerable<KeyValuePair<string, object?>> routeValues,
        string rawPath,
        string redactedPath,
        CompiledRedactionPatterns patterns,
        Action<string>? onRedacted = null)
    {
        var type = typeof(CompiledRedactionPatterns).Assembly.GetType("Lukdrasil.StepUpLogging.RequestPathRedaction");
        Assert.True(type is not null, "Lukdrasil.StepUpLogging declares no RequestPathRedaction");
        var method = type!.GetMethod("RedactRouteValues", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.True(method is not null, "RequestPathRedaction declares no static RedactRouteValues");
        try
        {
            return (IReadOnlyDictionary<string, object?>)method!.Invoke(null, [routeValues, rawPath, redactedPath, patterns, onRedacted])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static KeyValuePair<string, object?> Route(string key, object? value) => new(key, value);

    [Fact]
    public async Task SummaryPath_IsRedacted_WhenPatternMatchesPath()
    {
        var captured = await SendAsync(ActionLinksPath, Patterns(ActionLinksPattern));

        var summary = Assert.Single(captured.Summaries);
        Assert.Equal(RedactedActionLinksPath, Scalar(summary, "Path"));
    }

    [Fact]
    public async Task RouteParameters_PathCarriedToken_IsRedacted_InSummaryAndHttpEvent()
    {
        var captured = await SendAsync(ActionLinksPath, Patterns(ActionLinksPattern));

        Assert.Equal("[REDACTED]", RouteValue(Assert.Single(captured.Summaries), "token"));
        Assert.Equal("[REDACTED]", RouteValue(Assert.Single(captured.HttpEvents), "token"));
    }

    [Fact]
    public async Task HttpTarget_IsRedacted_OnLogRequestSpan()
    {
        var captured = await SendAsync(ActionLinksPath, Patterns(ActionLinksPattern));

        var span = Assert.Single(captured.LogRequestSpans);
        Assert.Equal(RedactedActionLinksPath, span.GetTagItem("http.target") as string);
    }

    [Fact]
    public async Task HttpEventRequestPath_IsRedacted_WhenPatternMatchesPath()
    {
        var captured = await SendAsync(ActionLinksPath, Patterns(ActionLinksPattern));

        var evt = Assert.Single(captured.HttpEvents);
        Assert.Equal(RedactedActionLinksPath, Scalar(evt, "RequestPath"));
    }

    [Fact]
    public async Task UnmatchedPath_IsExportedUnchanged_InSummaryHttpEventAndHttpTarget()
    {
        const string path = "/api/v2/Other/Thing/xyz789";

        var captured = await SendAsync(path, Patterns(ActionLinksPattern));

        Assert.Equal(path, Scalar(Assert.Single(captured.Summaries), "Path"));
        Assert.Equal(path, Scalar(Assert.Single(captured.HttpEvents), "RequestPath"));
        Assert.Equal(path, Assert.Single(captured.LogRequestSpans).GetTagItem("http.target") as string);
        Assert.Equal("xyz789", RouteValue(captured.Summaries[0], "token"));
    }

    [Fact]
    public async Task ExcludedRawPath_EmitsNoSummary_EvenWhenPatternRewritesPath()
    {
        var captured = await SendAsync(ActionLinksPath, Patterns(ActionLinksPattern), ["/api/v2/Public/ActionLinks/*"]);

        Assert.Equal(HttpStatusCode.OK, captured.Response.StatusCode);
        Assert.Empty(captured.Summaries);
        Assert.Empty(captured.HttpEvents);
    }

    [Fact]
    public async Task ActionLinksToken_LeaksIntoNoExportedField_WithDefaultOptions()
    {
        Assert.False(new StepUpLoggingOptions().RedactLogEventProperties);

        var captured = await SendAsync(ActionLinksPath, Patterns(ActionLinksPattern));

        var summary = Assert.Single(captured.Summaries);
        var httpEvent = Assert.Single(captured.HttpEvents);
        var span = Assert.Single(captured.LogRequestSpans);
        Assert.Equal("[REDACTED]", RouteValue(summary, "token"));
        Assert.Equal("[REDACTED]", RouteValue(httpEvent, "token"));
        var exported = string.Join("\n",
            summary.RenderMessage(),
            string.Join("\n", summary.Properties.Select(p => p.Value.ToString())),
            httpEvent.RenderMessage(),
            string.Join("\n", httpEvent.Properties.Select(p => p.Value.ToString())),
            string.Join("\n", span.TagObjects.Select(t => t.Value?.ToString())));
        Assert.Equal(0, Occurrences(exported, Token));
    }

    [Fact]
    public async Task PathRedactionTimeout_FailsClosed_AndStillEmitsOneSummary()
    {
        var secret = new string('a', 40);

        var captured = await SendAsync("/api/" + secret + "!", TimingOutPatterns());

        Assert.Equal(HttpStatusCode.OK, captured.Response.StatusCode);
        var summary = Assert.Single(captured.Summaries);
        Assert.Equal(CompiledRedactionPatterns.RedactionError, Scalar(summary, "Path"));
        Assert.Equal("[REDACTED]", RouteValue(summary, "id"));
        var httpEvent = Assert.Single(captured.HttpEvents);
        Assert.DoesNotContain(secret, Scalar(httpEvent, "RequestPath"));
        Assert.DoesNotContain(secret, RouteValue(httpEvent, "id"));
        Assert.DoesNotContain(secret, Assert.Single(captured.LogRequestSpans).GetTagItem("http.target") as string);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActionLinksToken_LeaksIntoNoExportedEvent_OfAddStepUpLoggingHost(bool redactLogEventProperties)
    {
        var events = await PathHost.SendAsync(
            ActionLinksPath,
            null,
            ("RedactionRegexes:0", ActionLinksPattern),
            ("RedactLogEventProperties", redactLogEventProperties ? "true" : "false"));

        var summary = Assert.Single(events, e => e.Properties.TryGetValue(LogProperties.IsRequestSummary, out var v) && v is ScalarValue { Value: true });
        Assert.Equal(RedactedActionLinksPath, Scalar(summary, "RequestPath"));
        Assert.Contains(events, e => Scalar(e, "SourceContext") == "Microsoft.AspNetCore.Hosting.Diagnostics" && Scalar(e, "Path") == RedactedActionLinksPath);
        Assert.Contains(events, e => e.MessageTemplate.Text == PathHost.HandlerInformation && Scalar(e, "RequestPath") == RedactedActionLinksPath);
        Assert.Contains(events, e => e.MessageTemplate.Text == PathHost.HandlerError && Scalar(e, "RequestPath") == RedactedActionLinksPath);
        var exported = string.Join("\n", events.Select(e => e.RenderMessage() + "\n" + string.Join("\n", e.Properties.Select(p => p.Value.ToString()))));
        Assert.Equal(0, Occurrences(exported, Token));
    }

    [Fact]
    public async Task RouteValuesInsideRedactedSpan_AreRedacted_EvenWhenNotSecret()
    {
        var captured = await SendAsync(ActionLinksPath, Patterns(ActionLinksPattern));

        var summary = Assert.Single(captured.Summaries);
        Assert.Equal("[REDACTED]", RouteValue(summary, "controller"));
        Assert.Equal("[REDACTED]", RouteValue(summary, "action"));
    }

    [Fact]
    public async Task RouteValueOutsideRedactedSpan_IsKept()
    {
        var captured = await SendAsync(ActionLinksPath, Patterns("(?i)/ActionLinks/[^/?]+"));

        var summary = Assert.Single(captured.Summaries);
        Assert.Equal("Public", RouteValue(summary, "controller"));
        Assert.Equal("Public", RouteValue(Assert.Single(captured.HttpEvents), "controller"));
    }

    [Fact]
    public async Task RedactedPath_NotesOnePathAndOneRouteTargetPerChangedValue()
    {
        var captured = await SendAsync(ActionLinksPath, Patterns(ActionLinksPattern));

        var span = Assert.Single(captured.RedactionSpans);
        var targets = ((span.GetTagItem("security.redaction_targets") as string) ?? string.Empty).Split(',');
        Assert.Single(targets, t => t == "path");
        Assert.Single(targets, t => t == "route:controller");
        Assert.Single(targets, t => t == "route:action");
        Assert.Single(targets, t => t == "route:token");
        Assert.Equal(4, span.GetTagItem("security.redaction_count"));
    }

    [Fact]
    public async Task UnchangedRequest_NotesNoRedaction()
    {
        var captured = await SendAsync("/api/v2/Other/Thing/xyz789", Patterns(ActionLinksPattern));

        Assert.Single(captured.Summaries);
        Assert.Empty(captured.RedactionSpans);
    }

    [Fact]
    public void RedactRouteValues_KeepsValueOutsideRedactedSpan_AndRedactsValueInsideIt()
    {
        var result = RedactRouteValues(
            [Route("version", "v2"), Route("token", Token)],
            ActionLinksPath,
            RedactedActionLinksPath,
            Patterns(ActionLinksPattern));

        Assert.Equal("v2", result["version"]);
        Assert.Equal("[REDACTED]", result["token"]);
    }

    [Fact]
    public void RedactRouteValues_RedactsValue_WhenOneOfItsOccurrencesWasRedacted()
    {
        var result = RedactRouteValues(
            [Route("id", "abc")],
            "/abc/secret/abc",
            "/abc[REDACTED]",
            Patterns("/secret/[^/]+"));

        Assert.Equal("[REDACTED]", result["id"]);
    }

    [Fact]
    public void RedactRouteValues_KeepsValue_WhenPathChangedElsewhere()
    {
        var result = RedactRouteValues(
            [Route("id", "abc")],
            "/abc/secret/xyz",
            "/abc[REDACTED]",
            Patterns("/secret/[^/]+"));

        Assert.Equal("abc", result["id"]);
    }

    [Fact]
    public void RedactRouteValues_NeverRedactsEmptyOrNullValue_ByCountRule()
    {
        var calls = new List<string>();

        var result = RedactRouteValues(
            [Route("empty", ""), Route("missing", null)],
            ActionLinksPath,
            RedactedActionLinksPath,
            Patterns(ActionLinksPattern),
            calls.Add);

        Assert.Equal("", result["empty"]);
        Assert.Equal("", result["missing"]);
        Assert.Empty(calls);
    }

    [Fact]
    public void RedactRouteValues_CountsOccurrencesOrdinally()
    {
        var result = RedactRouteValues(
            [Route("id", "abc")],
            "/abc/x",
            "/[REDACTED]/ABC",
            Patterns("never-matches"));

        Assert.Equal("[REDACTED]", result["id"]);
    }

    [Fact]
    public void RedactRouteValues_CountsOccurrencesWithoutOverlap()
    {
        var result = RedactRouteValues(
            [Route("id", "aa")],
            "/aaa",
            "/aa[REDACTED]",
            Patterns("never-matches"));

        Assert.Equal("aa", result["id"]);
    }

    [Fact]
    public void RedactRouteValues_CountRuleOverridesBareRedactionSentinel()
    {
        var secret = new string('a', 40) + "!";

        var result = RedactRouteValues(
            [Route("id", secret)],
            "/api/" + secret,
            CompiledRedactionPatterns.RedactionError,
            TimingOutPatterns());

        Assert.Equal("[REDACTED]", result["id"]);
    }

    [Fact]
    public void RedactRouteValues_KeepsBareRedaction_ForValueNotInPath()
    {
        var calls = new List<string>();

        var result = RedactRouteValues(
            [Route("id", "secret-xyz")],
            "/api/plain",
            "/api/plain",
            Patterns("secret-[a-z]+"),
            calls.Add);

        Assert.Equal("[REDACTED]", result["id"]);
        Assert.Equal(["route:id"], calls);
    }

    [Fact]
    public void RedactRouteValues_NotesEachChangedValueOnce_AndNoUnchangedValue()
    {
        var calls = new List<string>();

        var result = RedactRouteValues(
            [Route("version", "v2"), Route("controller", "Public"), Route("token", "secret-abc")],
            "/api/v2/Public/secret-abc",
            "/api/v2[REDACTED]",
            Patterns("/Public/[^/]+", "secret-[a-z]+"),
            calls.Add);

        Assert.Equal("v2", result["version"]);
        Assert.Equal("[REDACTED]", result["controller"]);
        Assert.Equal("[REDACTED]", result["token"]);
        Assert.Equal(["route:controller", "route:token"], calls.Order().ToList());
    }
}
