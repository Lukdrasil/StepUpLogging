using System.Net;
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
/// Pins that the request-completion log (the <c>EnrichDiagnosticContext</c> path) carries <c>ClientIp</c>
/// by the same rule as the request summary (ADR 0008): the connection address unless forwarded headers are trusted.
/// </summary>
public class RequestCompletionClientIpTests
{
    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static async Task<IHost> BuildServerAsync(Serilog.ILogger logger, bool trustForwardedHeaders, ClientIpMode clientIpLogging = ClientIpMode.Full, int status = 200, ClientIpMode onClientError = ClientIpMode.Off)
    {
        var opts = new StepUpLoggingOptions { TrustForwardedHeaders = trustForwardedHeaders, ClientIpLogging = clientIpLogging, ClientIpLoggingOnClientError = onClientError };

        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(Options.Create(opts));
                    services.AddSingleton(logger);
                    services.AddSingleton(sp => new StepUpLoggingController(opts, logger));
                    services.AddSingleton(new StepUpSettings(opts));
                    services.AddSingleton(new CompiledRedactionPatterns([]));
                    services.AddSingleton(new DiagnosticContext(logger));
                })
                .Configure(app =>
                {
                    app.Use(async (ctx, next) =>
                    {
                        ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
                        ctx.Connection.RemotePort = 51234;
                        await next();
                    });
                    app.UseStepUpRequestLogging();
                    app.Run(ctx =>
                    {
                        ctx.Response.StatusCode = status;
                        return ctx.Response.WriteAsync("ok");
                    });
                }))
            .Build();

        await host.StartAsync();
        return host;
    }

    [Theory]
    [InlineData(false, "203.0.113.7")]
    [InlineData(true, "198.51.100.9")]
    public async Task RequestCompletionEvent_CarriesClientIp(bool trustForwardedHeaders, string expected)
    {
        var capture = new CaptureSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();
        var previous = Log.Logger;
        Log.Logger = logger;
        try
        {
            using var host = await BuildServerAsync(logger, trustForwardedHeaders);
            using var client = host.GetTestClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/test");
            request.Headers.Add("X-Forwarded-For", "198.51.100.9, 10.0.0.1");

            var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            await Task.Delay(50, TestContext.Current.CancellationToken);

            var evt = capture.Events.LastOrDefault(e => e.Properties.ContainsKey("RequestPath"));
            Assert.NotNull(evt);
            Assert.True(evt!.Properties.TryGetValue("ClientIp", out var clientIp));
            Assert.Equal(expected, ((ScalarValue)clientIp).Value);
        }
        finally
        {
            Log.Logger = previous;
            logger.Dispose();
        }
    }

    private static async Task<LogEvent> CompletionEventAsync(ClientIpMode clientIpLogging, int status = 200, ClientIpMode onClientError = ClientIpMode.Off, bool trustForwardedHeaders = false)
    {
        var capture = new CaptureSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();
        var previous = Log.Logger;
        Log.Logger = logger;
        try
        {
            var path = $"/ip-{Guid.NewGuid():N}";
            using var host = await BuildServerAsync(logger, trustForwardedHeaders, clientIpLogging, status, onClientError);
            using var client = host.GetTestClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("X-Forwarded-For", "198.51.100.9, 10.0.0.1");
            request.Headers.Add("Forwarded", "for=198.51.100.9");
            request.Headers.Add("X-Real-IP", "198.51.100.9");
            request.Headers.Add("X-Original-For", "198.51.100.9:5678");
            request.Headers.Add("Authorization", "Bearer secret");
            request.Headers.Add("Cookie", "session=secret");

            var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(status, (int)response.StatusCode);
            await Task.Delay(50, TestContext.Current.CancellationToken);

            return Assert.Single(capture.Events.ToList(), e => e.Properties.TryGetValue("RequestPath", out var p) && p is ScalarValue { Value: string s } && s == path);
        }
        finally
        {
            Log.Logger = previous;
            logger.Dispose();
        }
    }

    private static Dictionary<string, string?> Headers(LogEvent evt)
        => ((DictionaryValue)evt.Properties["Headers"]).Elements
            .ToDictionary(e => (string)e.Key.Value!, e => (e.Value as ScalarValue)?.Value?.ToString(), StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData(ClientIpMode.Off, null)]
    [InlineData(ClientIpMode.Prefix, "203.0.113.0/24")]
    [InlineData(ClientIpMode.Full, "203.0.113.7")]
    public async Task RequestCompletionEvent_WritesClientIp_AsClientIpLoggingSays(ClientIpMode mode, string? expected)
    {
        var evt = await CompletionEventAsync(mode);

        Assert.Equal(expected, evt.Properties.TryGetValue("ClientIp", out var clientIp) ? ((ScalarValue)clientIp).Value : null);
    }

    [Theory]
    [InlineData(ClientIpMode.Off)]
    [InlineData(ClientIpMode.Prefix)]
    public async Task RequestCompletionEvent_RedactsTheIpHeaders_UnlessClientIpLoggingIsFull(ClientIpMode mode)
    {
        var headers = Headers(await CompletionEventAsync(mode));

        Assert.All(["X-Forwarded-For", "Forwarded", "X-Real-IP", "X-Original-For"], name => Assert.Equal("[REDACTED]", headers[name]));
    }

    [Fact]
    public async Task RequestCompletionEvent_LogsTheIpHeadersAsReceived_WhenClientIpLoggingIsFull()
    {
        var headers = Headers(await CompletionEventAsync(ClientIpMode.Full));

        Assert.Equal("198.51.100.9, 10.0.0.1", headers["X-Forwarded-For"]);
        Assert.Equal("198.51.100.9", headers["X-Real-IP"]);
    }

    private static object? Property(LogEvent evt, string name)
        => evt.Properties.TryGetValue(name, out var value) ? ((ScalarValue)value).Value : null;

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(499)]
    public async Task ClientErrorRequest_LogsTheFullClientIp_UnderOnClientErrorFull(int status)
    {
        var evt = await CompletionEventAsync(ClientIpMode.Off, status, ClientIpMode.Full);

        Assert.Equal("203.0.113.7", Property(evt, "ClientIp"));
    }

    [Theory]
    [InlineData(399)]
    [InlineData(500)]
    public async Task RequestOutsideTheClientErrorRange_KeepsClientIpLogging(int status)
    {
        var evt = await CompletionEventAsync(ClientIpMode.Off, status, ClientIpMode.Full);

        Assert.Null(Property(evt, "ClientIp"));
    }

    [Theory]
    [InlineData(ClientIpMode.Off, ClientIpMode.Off, null)]
    [InlineData(ClientIpMode.Off, ClientIpMode.Prefix, "203.0.113.0/24")]
    [InlineData(ClientIpMode.Off, ClientIpMode.Full, "203.0.113.7")]
    [InlineData(ClientIpMode.Prefix, ClientIpMode.Off, "203.0.113.0/24")]
    [InlineData(ClientIpMode.Prefix, ClientIpMode.Prefix, "203.0.113.0/24")]
    [InlineData(ClientIpMode.Prefix, ClientIpMode.Full, "203.0.113.7")]
    [InlineData(ClientIpMode.Full, ClientIpMode.Off, "203.0.113.7")]
    [InlineData(ClientIpMode.Full, ClientIpMode.Prefix, "203.0.113.7")]
    [InlineData(ClientIpMode.Full, ClientIpMode.Full, "203.0.113.7")]
    public async Task ClientErrorRequest_LogsTheHigherOfTheTwoModes(ClientIpMode clientIpLogging, ClientIpMode onClientError, string? expected)
    {
        var evt = await CompletionEventAsync(clientIpLogging, 403, onClientError);

        Assert.Equal(expected, Property(evt, "ClientIp"));
    }

    [Fact]
    public async Task ClientErrorRequest_LogsTheIpHeadersAsReceived_AndStillRedactsSecrets()
    {
        var headers = Headers(await CompletionEventAsync(ClientIpMode.Off, 401, ClientIpMode.Full));

        Assert.Equal("198.51.100.9, 10.0.0.1", headers["X-Forwarded-For"]);
        Assert.Equal("for=198.51.100.9", headers["Forwarded"]);
        Assert.Equal("198.51.100.9", headers["X-Real-IP"]);
        Assert.Equal("198.51.100.9:5678", headers["X-Original-For"]);
        Assert.Equal("[REDACTED]", headers["Authorization"]);
        Assert.Equal("[REDACTED]", headers["Cookie"]);
    }

    [Fact]
    public async Task SuccessfulRequest_RedactsTheIpHeaders_WhenOnlyOnClientErrorIsFull()
    {
        var headers = Headers(await CompletionEventAsync(ClientIpMode.Off, 200, ClientIpMode.Full));

        Assert.All(["X-Forwarded-For", "Forwarded", "X-Real-IP", "X-Original-For"], name => Assert.Equal("[REDACTED]", headers[name]));
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "203.0.113.7")]
    public async Task ClientErrorRequest_LogsTheConnectionAddress_OnlyWhenForwardedHeadersAreTrusted(bool trustForwardedHeaders, string? expected)
    {
        var evt = await CompletionEventAsync(ClientIpMode.Off, 403, ClientIpMode.Full, trustForwardedHeaders);

        Assert.Equal(expected, Property(evt, "RemoteIp"));
    }

    [Fact]
    public async Task ClientErrorRequest_LogsTheRemotePort_UnderAnEffectiveFull()
    {
        var evt = await CompletionEventAsync(ClientIpMode.Off, 403, ClientIpMode.Full);

        Assert.Equal(51234, Property(evt, "RemotePort"));
    }

    [Theory]
    [InlineData(ClientIpMode.Prefix, 403)]
    [InlineData(ClientIpMode.Full, 200)]
    public async Task RemotePort_IsNotLogged_UnlessTheEffectiveModeIsFull(ClientIpMode onClientError, int status)
    {
        var evt = await CompletionEventAsync(ClientIpMode.Off, status, onClientError);

        Assert.False(evt.Properties.ContainsKey("RemotePort"));
    }
}
