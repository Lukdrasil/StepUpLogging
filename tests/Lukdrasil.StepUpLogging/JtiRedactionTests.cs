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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Extensions.Hosting;
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

    private const string Uuid = "3f2b8c1e-9a4d-4e6f-8b7a-1c2d3e4f5a6b";
    private const string UuidPattern = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

    private static async Task<IHost> BuildServerAsync(Serilog.ILogger logger, string jtiClaim, string[]? exempt = null, bool alwaysLogRequestSummary = false)
    {
        var opts = new StepUpLoggingOptions
        {
            RedactionRegexes = new[] { "secret-[A-Za-z0-9]+", UuidPattern },
            RedactionExemptProperties = exempt ?? [],
            AlwaysLogRequestSummary = alwaysLogRequestSummary,
        };

        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(Options.Create(opts));
                    services.AddSingleton(logger);
                    services.AddSingleton(sp => new StepUpLoggingController(opts, logger));
                    var patterns = opts.RedactionRegexes
                        .Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                        .ToArray();
                    services.AddSingleton(new CompiledRedactionPatterns(patterns, opts.RedactionExemptProperties));
                    services.AddSingleton(new DiagnosticContext(logger));
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
                }))
            .Build();

        await host.StartAsync();
        return host;
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
            using var host = await BuildServerAsync(logger, "secret-jti999");
            using var client = host.GetTestClient();

            var response = await client.GetAsync("/test", TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            await Task.Delay(50, TestContext.Current.CancellationToken);

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

    [Fact]
    public async Task Jti_Exempt_IsKept_OnEnrichDiagnosticContextPath()
    {
        var capture = new CaptureSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();
        var previous = Log.Logger;
        Log.Logger = logger;
        try
        {
            using var host = await BuildServerAsync(logger, "onrtac:" + Uuid, ["Jti"]);
            using var client = host.GetTestClient();

            var response = await client.GetAsync("/" + Uuid, TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            await Task.Delay(50, TestContext.Current.CancellationToken);

            var evt = FindRequestFinishedEvent(capture);
            Assert.NotNull(evt);
            Assert.Equal("onrtac:" + Uuid, ((ScalarValue)evt!.Properties["Jti"]).Value);
            Assert.Contains("[REDACTED]", (string)((ScalarValue)evt.Properties["RequestPath"]).Value!);
        }
        finally
        {
            Log.Logger = previous;
            logger.Dispose();
        }
    }

    [Fact]
    public async Task Jti_Exempt_IsKept_OnRequestSummary()
    {
        var capture = new CaptureSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();
        var previous = Log.Logger;
        Log.Logger = logger;
        try
        {
            using var host = await BuildServerAsync(logger, "onrtac:" + Uuid, ["Jti"], alwaysLogRequestSummary: true);
            using var client = host.GetTestClient();

            var response = await client.GetAsync("/" + Uuid, TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            await Task.Delay(50, TestContext.Current.CancellationToken);

            var summary = Assert.Single(capture.Events, e => e.Properties.ContainsKey(LogProperties.IsRequestSummary));
            Assert.Equal("onrtac:" + Uuid, ((ScalarValue)summary.Properties["Jti"]).Value);
            Assert.Contains("[REDACTED]", (string)((ScalarValue)summary.Properties["Path"]).Value!);
        }
        finally
        {
            Log.Logger = previous;
            logger.Dispose();
        }
    }
}
