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

    private static async Task<IHost> BuildServerAsync(Serilog.ILogger logger, bool trustForwardedHeaders, ClientIpMode clientIpLogging = ClientIpMode.Full)
    {
        var opts = new StepUpLoggingOptions { TrustForwardedHeaders = trustForwardedHeaders, ClientIpLogging = clientIpLogging };

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
                        await next();
                    });
                    app.UseStepUpRequestLogging();
                    app.Run(ctx => ctx.Response.WriteAsync("ok"));
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

    private static async Task<LogEvent> CompletionEventAsync(ClientIpMode clientIpLogging)
    {
        var capture = new CaptureSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();
        var previous = Log.Logger;
        Log.Logger = logger;
        try
        {
            var path = $"/ip-{Guid.NewGuid():N}";
            using var host = await BuildServerAsync(logger, false, clientIpLogging);
            using var client = host.GetTestClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("X-Forwarded-For", "198.51.100.9, 10.0.0.1");
            request.Headers.Add("Forwarded", "for=198.51.100.9");
            request.Headers.Add("X-Real-IP", "198.51.100.9");
            request.Headers.Add("X-Original-For", "198.51.100.9:5678");

            var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
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
}
