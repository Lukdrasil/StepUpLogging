using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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
using Serilog.Extensions.Hosting;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Characterization tests pinning that <c>UseStepUpRequestLogging</c>'s <c>EnrichDiagnosticContext</c>
/// redacts <c>QueryString</c> and <c>RouteParameters</c> through the real middleware pipeline, not just
/// through <see cref="CompiledRedactionPatterns.Redact"/> in isolation.
/// </summary>
public class QueryStringAndRouteParameterRedactionTests
{
    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static async Task<IHost> BuildServerAsync(Serilog.ILogger logger)
    {
        var opts = new StepUpLoggingOptions
        {
            RedactionRegexes = new[] { "secret-[A-Za-z0-9]+" }
        };

        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton(Options.Create(opts));
                    services.AddSingleton(logger);
                    services.AddSingleton(sp => new StepUpLoggingController(opts, logger));
                    var patterns = opts.RedactionRegexes
                        .Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                        .ToArray();
                    services.AddSingleton(new CompiledRedactionPatterns(patterns));
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
                    });
                }))
            .Build();

        await host.StartAsync();
        return host;
    }

    private static LogEvent? FindRequestFinishedEvent(CaptureSink capture) =>
        capture.Events.LastOrDefault(e => e.Properties.ContainsKey("QueryString"));

    [Fact]
    public async Task QueryString_IsRedacted_ThroughRealMiddleware()
    {
        var capture = new CaptureSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();
        var previous = Log.Logger;
        Log.Logger = logger;
        try
        {
            using var host = await BuildServerAsync(logger);
            using var client = host.GetTestClient();

            var response = await client.GetAsync("/api/plain?token=secret-xyz789");
            response.EnsureSuccessStatusCode();
            await Task.Delay(50);

            var evt = FindRequestFinishedEvent(capture);
            Assert.NotNull(evt);
            var qs = ((ScalarValue)evt!.Properties["QueryString"]).Value as string;
            Assert.NotNull(qs);
            Assert.Contains("[REDACTED]", qs);
            Assert.DoesNotContain("secret-xyz789", qs);
        }
        finally
        {
            Log.Logger = previous;
            logger.Dispose();
        }
    }

    [Fact]
    public async Task RouteParameters_AreRedacted_ThroughRealMiddleware()
    {
        var capture = new CaptureSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();
        var previous = Log.Logger;
        Log.Logger = logger;
        try
        {
            using var host = await BuildServerAsync(logger);
            using var client = host.GetTestClient();

            var response = await client.GetAsync("/api/secret-abc123");
            response.EnsureSuccessStatusCode();
            await Task.Delay(50);

            var evt = FindRequestFinishedEvent(capture);
            Assert.NotNull(evt);
            Assert.True(evt!.Properties.ContainsKey("RouteParameters"));
            var routeParamsRendered = evt.Properties["RouteParameters"].ToString();
            Assert.Contains("[REDACTED]", routeParamsRendered);
            Assert.DoesNotContain("secret-abc123", routeParamsRendered);
        }
        finally
        {
            Log.Logger = previous;
            logger.Dispose();
        }
    }
}
