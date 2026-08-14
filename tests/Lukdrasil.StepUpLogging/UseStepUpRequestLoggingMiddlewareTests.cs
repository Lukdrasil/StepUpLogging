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
            public LogEvent? LastEvent { get; private set; }
            public void Emit(LogEvent logEvent)
            {
                LastEvent = logEvent;
            }
        }

        [Fact(DisplayName = "Middleware_EmitsRequestSummary_WhenAlwaysLogEnabled")]
        public async Task Middleware_EmitsRequestSummary_WhenAlwaysLogEnabled()
        {
            var capture = new CaptureSink();
            var summaryLogger = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();

            using var host = new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        // Register options with AlwaysLogRequestSummary true
                        var opts = new StepUpLoggingOptions { AlwaysLogRequestSummary = true, RequestSummaryLevel = "Information" };
                        services.AddSingleton(Options.Create(opts));
                        services.AddSingleton<Serilog.ILogger>(summaryLogger);
                        services.AddSingleton(sp => new StepUpLoggingController(opts, summaryLogger));
                        // Register compiled redaction patterns required by middleware
                        var patterns = opts.RedactionRegexes
                            .Where(p => !string.IsNullOrWhiteSpace(p))
                            .Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                            .ToArray();
                        services.AddSingleton(new CompiledRedactionPatterns(patterns));
                        // Register Serilog DiagnosticContext required by Serilog.AspNetCore.RequestLoggingMiddleware
                        services.AddSingleton(new DiagnosticContext(summaryLogger));
                    })
                    .Configure(app =>
                    {
                        // Add middleware
                        app.UseStepUpRequestLogging();

                        app.Run(async ctx =>
                        {
                            ctx.Response.StatusCode = 200;
                            await ctx.Response.WriteAsync("ok");
                        });
                    }))
                .Build();

            await host.StartAsync();
            using var client = host.GetTestClient();

            var res = await client.GetAsync("/api/test");
            res.EnsureSuccessStatusCode();

            // Allow some time for logging to propagate
            await Task.Delay(50);

            Assert.NotNull(capture.LastEvent);
            Assert.True(capture.LastEvent!.Properties.ContainsKey("IsRequestSummary"));
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
            await client.GetAsync("/api/test");
            await Task.Delay(50);

            Assert.NotNull(capture.LastEvent);
            Assert.True(capture.LastEvent!.Properties.TryGetValue("Jti", out var jtiProp));
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
            await client.GetAsync("/api/test");
            await Task.Delay(50);

            Assert.NotNull(capture.LastEvent);
            Assert.False(capture.LastEvent!.Properties.ContainsKey("Jti"));
        }

        [Fact(DisplayName = "AlwaysLogSummary_OmitsJti_WhenUnauthenticated")]
        public async Task AlwaysLogSummary_OmitsJti_WhenUnauthenticated()
        {
            var capture = new CaptureSink();
            using var host = await BuildHostWithSummary(capture);

            using var client = host.GetTestClient();
            await client.GetAsync("/api/test");
            await Task.Delay(50);

            Assert.NotNull(capture.LastEvent);
            Assert.False(capture.LastEvent!.Properties.ContainsKey("Jti"));
        }
    }
}
