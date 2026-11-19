using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Http;
using System.Text.RegularExpressions;
using Lukdrasil.StepUpLogging;
using Serilog.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests
{
    public class UseStepUpRequestLoggingMiddlewareTests
    {
        private sealed class CaptureSink : ILogEventSink
        {
            private readonly ConcurrentQueue<LogEvent> _events = new();
            public LogEvent[] Summaries => _events.Where(e => LogProperties.HasFlag(e, LogProperties.IsRequestSummary)).ToArray();
            public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
        }

        [Fact(DisplayName = "RequestSummaryLevel_FromConfiguration_IsTheLevelOfTheExportedSummary")]
        public async Task RequestSummaryLevel_FromConfiguration_IsTheLevelOfTheExportedSummary()
        {
            var captureKey = Guid.NewGuid().ToString("N");
            var settings = new Dictionary<string, string?>
            {
                ["SerilogStepUp:EnableOtlpExporter"] = "false",
                ["SerilogStepUp:AlwaysLogRequestSummary"] = "true",
                ["SerilogStepUp:RequestSummaryLevel"] = "Debug",
                ["Serilog:Using:0"] = typeof(KeyedCaptureSink).Assembly.GetName().Name,
                ["Serilog:WriteTo:0:Name"] = nameof(KeyedCaptureSink.StepUpCapture),
                ["Serilog:WriteTo:0:Args:key"] = captureKey,
            };

            try
            {
                var builder = WebApplication.CreateBuilder();
                builder.WebHost.UseTestServer();
                builder.Configuration.AddInMemoryCollection(settings);
                builder.AddStepUpLogging();

                await using (var app = builder.Build())
                {
                    app.UseStepUpRequestLogging();
                    app.MapGet("/ping", () => "pong");

                    await app.StartAsync(TestContext.Current.CancellationToken);
                    using var client = app.GetTestClient();
                    using var response = await client.GetAsync("/ping", TestContext.Current.CancellationToken);

                    await Task.Delay(200, TestContext.Current.CancellationToken);
                    await app.StopAsync(TestContext.Current.CancellationToken);
                }

                var summary = Assert.Single(KeyedCaptureSink.Events(captureKey), e => LogProperties.HasFlag(e, LogProperties.IsRequestSummary));
                Assert.Equal(LogEventLevel.Debug, summary.Level);
            }
            finally
            {
                KeyedCaptureSink.Forget(captureKey);
            }
        }

        private static async Task<IHost> BuildHostWithSummary(CaptureSink capture, Action<HttpContext>? configureContext = null)
        {
            var summaryLogger = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();
            var host = new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        var opts = new StepUpLoggingOptions { AlwaysLogRequestSummary = true, RequestSummaryLevel = "Information" };
                        services.AddSingleton(Options.Create(opts));
                        services.AddSingleton<Serilog.ILogger>(summaryLogger);
                        services.AddSingleton(sp => new StepUpLoggingController(opts, summaryLogger));
                        services.AddSingleton(new StepUpSettings(opts));
                        var patterns = opts.RedactionRegexes
                            .Where(p => !string.IsNullOrWhiteSpace(p))
                            .Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                            .ToArray();
                        services.AddSingleton(new CompiledRedactionPatterns(patterns));
                        services.AddSingleton(new DiagnosticContext(summaryLogger));
                    })
                    .Configure(app =>
                    {
                        if (configureContext != null)
                        {
                            app.Use(async (ctx, next) =>
                            {
                                configureContext(ctx);
                                await next();
                            });
                        }
                        app.UseStepUpRequestLogging();
                        app.Run(async ctx =>
                        {
                            ctx.Response.StatusCode = 200;
                            await ctx.Response.WriteAsync("ok");
                        });
                    }))
                .Build();

            await host.StartAsync();
            return host;
        }

        [Fact(DisplayName = "AlwaysLogSummary_IncludesJti_WhenJtiClaimPresent")]
        public async Task AlwaysLogSummary_IncludesJti_WhenJtiClaimPresent()
        {
            var capture = new CaptureSink();
            using var host = await BuildHostWithSummary(capture, ctx =>
            {
                ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim("jti", "test-jti-abc") }, "test"));
            });

            using var client = host.GetTestClient();
            await client.GetAsync("/api/test", TestContext.Current.CancellationToken);
            await Task.Delay(50, TestContext.Current.CancellationToken);

            var summary = Assert.Single(capture.Summaries);
            Assert.True(summary.Properties.TryGetValue("Jti", out var jtiProp));
            Assert.Equal("\"test-jti-abc\"", jtiProp.ToString());
        }

        [Fact(DisplayName = "AlwaysLogSummary_OmitsJti_WhenNoJtiClaim")]
        public async Task AlwaysLogSummary_OmitsJti_WhenNoJtiClaim()
        {
            var capture = new CaptureSink();
            using var host = await BuildHostWithSummary(capture, ctx =>
            {
                ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim("sub", "user-123") }, "test"));
            });

            using var client = host.GetTestClient();
            await client.GetAsync("/api/test", TestContext.Current.CancellationToken);
            await Task.Delay(50, TestContext.Current.CancellationToken);

            var summary = Assert.Single(capture.Summaries);
            Assert.False(summary.Properties.ContainsKey("Jti"));
        }

        [Fact(DisplayName = "AlwaysLogSummary_OmitsJti_WhenUnauthenticated")]
        public async Task AlwaysLogSummary_OmitsJti_WhenUnauthenticated()
        {
            var capture = new CaptureSink();
            using var host = await BuildHostWithSummary(capture);

            using var client = host.GetTestClient();
            await client.GetAsync("/api/test", TestContext.Current.CancellationToken);
            await Task.Delay(50, TestContext.Current.CancellationToken);

            var summary = Assert.Single(capture.Summaries);
            Assert.False(summary.Properties.ContainsKey("Jti"));
        }
    }
}
