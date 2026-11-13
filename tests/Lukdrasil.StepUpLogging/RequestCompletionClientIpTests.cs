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

    private static async Task<IHost> BuildServerAsync(Serilog.ILogger logger, bool trustForwardedHeaders)
    {
        var opts = new StepUpLoggingOptions { TrustForwardedHeaders = trustForwardedHeaders };

        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(Options.Create(opts));
                    services.AddSingleton(logger);
                    services.AddSingleton(sp => new StepUpLoggingController(opts, logger));
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
}
