using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Pins that the <c>jti</c> token identifier is redacted on the <c>EnrichDiagnosticContext</c> path
/// (the request-completion log), mirroring the summary-path redaction covered elsewhere. <c>jti</c>
/// originates in a user-presented token and must never be logged verbatim.
/// </summary>
public class JtiRedactionTests
{
    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static TestServer BuildServer(CaptureSink capture, Serilog.ILogger logger, string jtiClaim)
    {
        var opts = new StepUpLoggingOptions { RedactionRegexes = new[] { "secret-[A-Za-z0-9]+" } };

        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(Options.Create(opts));
                services.AddSingleton(logger);
                services.AddSingleton(sp => new StepUpLoggingController(opts, logger));
                var patterns = opts.RedactionRegexes
                    .Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                    .ToArray();
                services.AddSingleton(new CompiledRedactionPatterns(patterns));
                var diagType = Type.GetType("Serilog.Extensions.Hosting.DiagnosticContext, Serilog.Extensions.Hosting")
                               ?? Type.GetType("Serilog.AspNetCore.DiagnosticContext, Serilog.AspNetCore");
                var ctor = diagType!.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
                var args = ctor.GetParameters().Select(p =>
                    p.ParameterType == typeof(Serilog.ILogger) ? (object?)logger
                    : p.HasDefaultValue ? p.DefaultValue
                    : null).ToArray();
                services.AddSingleton(diagType, ctor.Invoke(args));
            })
            .Configure(app =>
            {
                app.Use(async (ctx, next) =>
                {
                    ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("jti", jtiClaim) }, "test"));
                    await next();
                });
                app.UseStepUpRequestLogging();
                app.Run(async ctx =>
                {
                    ctx.Response.StatusCode = 200;
                    await ctx.Response.WriteAsync("ok");
                });
            });

        return new TestServer(builder);
    }

    private static LogEvent? FindRequestFinishedEvent(CaptureSink capture) =>
        capture.Events.LastOrDefault(e => e.Properties.ContainsKey("Jti"));

    [Fact]
    public async Task Jti_IsRedacted_OnEnrichDiagnosticContextPath()
    {
        var capture = new CaptureSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();
        var previous = Log.Logger;
        Log.Logger = logger;
        try
        {
            using var server = BuildServer(capture, logger, "secret-jti999");
            using var client = server.CreateClient();

            var response = await client.GetAsync("/test");
            response.EnsureSuccessStatusCode();
            await Task.Delay(50);

            var evt = FindRequestFinishedEvent(capture);
            Assert.NotNull(evt);
            var jti = ((ScalarValue)evt!.Properties["Jti"]).Value as string;
            Assert.NotNull(jti);
            Assert.Contains("[REDACTED]", jti);
            Assert.DoesNotContain("secret-jti999", jti);
        }
        finally
        {
            Log.Logger = previous;
            logger.Dispose();
        }
    }
}
