using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests
{
    public class AdditionalSensitiveHeaderRedactionTests
    {
        private sealed class CaptureSink : ILogEventSink
        {
            public List<LogEvent> Events { get; } = new();
            public void Emit(LogEvent logEvent) => Events.Add(logEvent);
        }

        // Regression for the case-insensitive matching of AdditionalSensitiveHeaders:
        // a configured header name must be redacted regardless of the casing the client sends.
        [Fact(DisplayName = "AdditionalSensitiveHeader_IsRedacted_WhenClientCasingDiffers")]
        public async Task AdditionalSensitiveHeader_IsRedacted_WhenClientCasingDiffers()
        {
            const string secret = "super-secret-token-value";
            var capture = new CaptureSink();
            var completionLogger = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();

            var previous = Log.Logger;
            Log.Logger = completionLogger;
            try
            {
                var opts = new StepUpLoggingOptions
                {
                    AdditionalSensitiveHeaders = new[] { "X-Api-Token" }
                };

                var builder = new WebHostBuilder()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton(Options.Create(opts));
                        services.AddSingleton<Serilog.ILogger>(completionLogger);
                        services.AddSingleton(sp => new StepUpLoggingController(opts, completionLogger));
                        var patterns = opts.RedactionRegexes
                            .Where(p => !string.IsNullOrWhiteSpace(p))
                            .Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                            .ToArray();
                        services.AddSingleton(new CompiledRedactionPatterns(patterns));
                        var diagType = Type.GetType("Serilog.Extensions.Hosting.DiagnosticContext, Serilog.Extensions.Hosting")
                                       ?? Type.GetType("Serilog.AspNetCore.DiagnosticContext, Serilog.AspNetCore");
                        var ctor = diagType!.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
                        var args = ctor.GetParameters().Select(p =>
                            p.ParameterType == typeof(Serilog.ILogger) ? (object?)completionLogger
                            : p.HasDefaultValue ? p.DefaultValue
                            : null).ToArray();
                        services.AddSingleton(diagType, ctor.Invoke(args));
                    })
                    .Configure(app =>
                    {
                        app.UseStepUpRequestLogging();
                        app.Run(async ctx =>
                        {
                            ctx.Response.StatusCode = 200;
                            await ctx.Response.WriteAsync("ok");
                        });
                    });

                using var server = new TestServer(builder);
                using var client = server.CreateClient();
                // Client sends a different casing than the configured "X-Api-Token".
                client.DefaultRequestHeaders.Add("x-api-token", secret);
                await client.GetAsync("/api/test");
                await Task.Delay(50);

                // Log.Logger is global and xunit runs test classes in parallel, so other tests'
                // completion events can land in this sink. Select the event carrying our header.
                var mine = capture.Events
                    .Where(e => e.Properties.TryGetValue("Headers", out var h)
                                && h.ToString().Contains("api-token", StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.Properties["Headers"].ToString())
                    .ToList();
                Assert.NotEmpty(mine);
                Assert.All(mine, rendered =>
                {
                    Assert.Contains("[REDACTED]", rendered);
                    Assert.DoesNotContain(secret, rendered);
                });
            }
            finally
            {
                Log.Logger = previous;
                completionLogger.Dispose();
            }
        }
    }
}
