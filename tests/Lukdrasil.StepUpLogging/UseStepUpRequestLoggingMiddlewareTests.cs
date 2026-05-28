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

            var builder = new WebHostBuilder()
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
                    // Some Serilog versions expose DiagnosticContext in different assemblies/namespaces; register via reflection to be robust in tests.
                    var diagType = Type.GetType("Serilog.Extensions.Hosting.DiagnosticContext, Serilog.Extensions.Hosting")
                                   ?? Type.GetType("Serilog.AspNetCore.DiagnosticContext, Serilog.AspNetCore");
                    if (diagType != null)
                    {
                        var ctor = diagType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
                        object instance;
                        if (ctor != null)
                        {
                            var parms = ctor.GetParameters();
                            var args = parms.Select(p =>
                            {
                                if (p.ParameterType == typeof(Serilog.ILogger) || p.ParameterType.FullName == "Serilog.Core.Logger") return (object)summaryLogger;
                                if (p.HasDefaultValue) return p.DefaultValue;
                                return null;
                            }).ToArray();
                            try { instance = ctor.Invoke(args); }
                            catch { instance = Activator.CreateInstance(diagType); }
                        }
                        else
                        {
                            instance = Activator.CreateInstance(diagType);
                        }
                        services.AddSingleton(diagType, instance);
                    } else {
                        // Fallback: register the DiagnosticContext from Serilog.Extensions.Hosting if available by type name
                        try { services.AddSingleton<DiagnosticContext>(sp => new DiagnosticContext(summaryLogger)); } catch { }
                    }
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
                });

            using var server = new TestServer(builder);
            using var client = server.CreateClient();

            var res = await client.GetAsync("/api/test");
            res.EnsureSuccessStatusCode();

            // Allow some time for logging to propagate
            await Task.Delay(50);

            Assert.NotNull(capture.LastEvent);
            Assert.True(capture.LastEvent!.Properties.ContainsKey("IsRequestSummary"));
        }

        private static IWebHostBuilder BuildHostWithSummary(CaptureSink capture, Action<HttpContext>? configureContext = null)
        {
            var summaryLogger = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();
            var builder = new WebHostBuilder()
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
                    var diagType = Type.GetType("Serilog.Extensions.Hosting.DiagnosticContext, Serilog.Extensions.Hosting")
                                   ?? Type.GetType("Serilog.AspNetCore.DiagnosticContext, Serilog.AspNetCore");
                    if (diagType != null)
                    {
                        var ctor = diagType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
                        object instance;
                        if (ctor != null)
                        {
                            var parms = ctor.GetParameters();
                            var args = parms.Select(p =>
                            {
                                if (p.ParameterType == typeof(Serilog.ILogger) || p.ParameterType.FullName == "Serilog.Core.Logger") return (object)summaryLogger;
                                if (p.HasDefaultValue) return p.DefaultValue;
                                return null;
                            }).ToArray();
                            try { instance = ctor.Invoke(args); }
                            catch { instance = Activator.CreateInstance(diagType)!; }
                        }
                        else
                        {
                            instance = Activator.CreateInstance(diagType)!;
                        }
                        services.AddSingleton(diagType, instance);
                    }
                    else
                    {
                        try { services.AddSingleton<DiagnosticContext>(sp => new DiagnosticContext(summaryLogger)); } catch { }
                    }
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
                });
            return builder;
        }

        [Fact(DisplayName = "AlwaysLogSummary_IncludesJti_WhenJtiClaimPresent")]
        public async Task AlwaysLogSummary_IncludesJti_WhenJtiClaimPresent()
        {
            var capture = new CaptureSink();
            var builder = BuildHostWithSummary(capture, ctx =>
            {
                ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim("jti", "test-jti-abc") }, "test"));
            });

            using var server = new TestServer(builder);
            using var client = server.CreateClient();
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
            var builder = BuildHostWithSummary(capture, ctx =>
            {
                ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim("sub", "user-123") }, "test"));
            });

            using var server = new TestServer(builder);
            using var client = server.CreateClient();
            await client.GetAsync("/api/test");
            await Task.Delay(50);

            Assert.NotNull(capture.LastEvent);
            Assert.False(capture.LastEvent!.Properties.ContainsKey("Jti"));
        }

        [Fact(DisplayName = "AlwaysLogSummary_OmitsJti_WhenUnauthenticated")]
        public async Task AlwaysLogSummary_OmitsJti_WhenUnauthenticated()
        {
            var capture = new CaptureSink();
            var builder = BuildHostWithSummary(capture);

            using var server = new TestServer(builder);
            using var client = server.CreateClient();
            await client.GetAsync("/api/test");
            await Task.Delay(50);

            Assert.NotNull(capture.LastEvent);
            Assert.False(capture.LastEvent!.Properties.ContainsKey("Jti"));
        }
    }
}
