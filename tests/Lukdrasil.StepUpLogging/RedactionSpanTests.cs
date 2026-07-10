using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
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
/// Verifies that <c>EnrichDiagnosticContext</c> emits at most one aggregate <c>ApplyRedaction</c> span
/// per request (ADR 0009) rather than one span per redacted field.
/// </summary>
public class RedactionSpanTests
{
    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static TestServer BuildServer(CaptureSink capture, Serilog.ILogger logger)
    {
        var opts = new StepUpLoggingOptions { EnableActivityInstrumentation = true };

        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(Options.Create(opts));
                services.AddSingleton(logger);
                services.AddSingleton(sp => new StepUpLoggingController(opts, logger));
                services.AddSingleton(new CompiledRedactionPatterns(Array.Empty<Regex>()));
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
                app.UseStepUpRequestLogging();
                app.Run(async ctx =>
                {
                    ctx.Response.StatusCode = 200;
                    await ctx.Response.WriteAsync("ok");
                });
            });

        return new TestServer(builder);
    }

    private static (List<Activity> Redactions, ActivityListener Listener) ListenForRedactionSpans()
    {
        var activities = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == StepUpLoggingExtensions.RequestLoggingActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                if (a.OperationName == "ApplyRedaction")
                {
                    lock (activities) activities.Add(a);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        return (activities, listener);
    }

    [Fact]
    public async Task TwoSensitiveHeaders_ProduceExactlyOneRedactionSpan_WithCountTwo()
    {
        var capture = new CaptureSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();
        var previous = Log.Logger;
        Log.Logger = logger;
        var (redactions, listener) = ListenForRedactionSpans();
        try
        {
            using var server = BuildServer(capture, logger);
            using var client = server.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/test");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer xyz");
            request.Headers.TryAddWithoutValidation("Cookie", "session=abc");

            await client.SendAsync(request);
            await Task.Delay(50);

            Assert.Single(redactions);
            var span = redactions[0];
            Assert.Equal(true, span.GetTagItem("security.redaction_applied"));
            Assert.Equal(2, span.GetTagItem("security.redaction_count"));
            var targets = span.GetTagItem("security.redaction_targets") as string;
            Assert.NotNull(targets);
            Assert.Contains("header:Authorization", targets);
            Assert.Contains("header:Cookie", targets);
            Assert.Null(span.GetTagItem("security.redaction_type"));
        }
        finally
        {
            listener.Dispose();
            Log.Logger = previous;
            logger.Dispose();
        }
    }

    [Fact]
    public async Task NoRedactableContent_ProducesNoRedactionSpan()
    {
        var capture = new CaptureSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();
        var previous = Log.Logger;
        Log.Logger = logger;
        var (redactions, listener) = ListenForRedactionSpans();
        try
        {
            using var server = BuildServer(capture, logger);
            using var client = server.CreateClient();

            await client.GetAsync("http://localhost/test");
            await Task.Delay(50);

            Assert.Empty(redactions);
        }
        finally
        {
            listener.Dispose();
            Log.Logger = previous;
            logger.Dispose();
        }
    }
}
