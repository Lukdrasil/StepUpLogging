using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

public class StaticLoggerTests : IDisposable
{
    private readonly Serilog.ILogger _previousLogger = Log.Logger;

    public void Dispose() => Log.Logger = _previousLogger;

    private sealed class CaptureSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
        public bool Contains(string marker) => Events.Any(e => e.RenderMessage().Contains(marker, StringComparison.Ordinal));
    }

    private static HostApplicationBuilder CreateHostBuilder(Dictionary<string, string?>? extra = null)
    {
        var builder = Host.CreateApplicationBuilder();
        var config = new Dictionary<string, string?>
        {
            ["SerilogStepUp:EnableOtlpExporter"] = "false",
        };
        foreach (var (key, value) in extra ?? [])
        {
            config[key] = value;
        }
        builder.Configuration.AddInMemoryCollection(config);
        return builder;
    }

    [Fact]
    public void DefaultOptions_LeaveStaticLoggerUnchanged_AfterBuildAndLoggerResolution()
    {
        var before = Log.Logger;
        var builder = CreateHostBuilder();
        builder.AddStepUpLogging();

        using var host = builder.Build();
        Assert.Same(before, Log.Logger);

        host.Services.GetRequiredService<ILogger<StaticLoggerTests>>().LogInformation("resolved");
        Assert.Same(before, Log.Logger);
    }

    [Fact]
    public void SetStaticLoggerViaConfigureOptions_ReplacesStaticLogger()
    {
        var before = Log.Logger;
        var builder = CreateHostBuilder();
        builder.AddStepUpLogging(configureOptions: opts => opts.SetStaticLogger = true);

        using var host = builder.Build();

        Assert.NotSame(before, Log.Logger);
    }

    [Fact]
    public void SetStaticLoggerViaConfiguration_RoutesStaticLogCallsToConfiguredSink()
    {
        var capture = new CaptureSink();
        var builder = CreateHostBuilder(new() { ["SerilogStepUp:SetStaticLogger"] = "true" });
        builder.AddStepUpLogging((_, lc) => lc.WriteTo.Sink(capture));
        var marker = "STATIC_" + Guid.NewGuid().ToString("N");

        using var host = builder.Build();
        Log.Information(marker);

        Assert.True(capture.Contains(marker));
    }

    [Fact]
    public void TwoHostsWithDefaultOptions_EachLogsToItsOwnSink_AndSurvivesTheOthersDisposal()
    {
        var captureA = new CaptureSink();
        var captureB = new CaptureSink();
        var builderA = CreateHostBuilder();
        builderA.AddStepUpLogging((_, lc) => lc.WriteTo.Sink(captureA));
        var builderB = CreateHostBuilder();
        builderB.AddStepUpLogging((_, lc) => lc.WriteTo.Sink(captureB));
        var markerA = "HOST_A_" + Guid.NewGuid().ToString("N");
        var markerB = "HOST_B_" + Guid.NewGuid().ToString("N");
        var markerAfterDispose = "HOST_B_AFTER_" + Guid.NewGuid().ToString("N");

        var hostA = builderA.Build();
        using var hostB = builderB.Build();
        var loggerB = hostB.Services.GetRequiredService<ILogger<StaticLoggerTests>>();

        hostA.Services.GetRequiredService<ILogger<StaticLoggerTests>>().LogInformation(markerA);
        loggerB.LogInformation(markerB);

        Assert.True(captureA.Contains(markerA));
        Assert.False(captureA.Contains(markerB));
        Assert.True(captureB.Contains(markerB));
        Assert.False(captureB.Contains(markerA));

        hostA.Dispose();
        loggerB.LogInformation(markerAfterDispose);

        Assert.True(captureB.Contains(markerAfterDispose));
    }

    [Fact]
    public async Task DefaultOptions_RequestLogging_ReachesHostLogger_WithStaticLoggerUnchanged()
    {
        using var sentinel = new LoggerConfiguration().CreateLogger();
        Log.Logger = sentinel;
        var capture = new CaptureSink();
        var summaryFile = Path.Combine(Path.GetTempPath(), $"stepup-static-summary-{Guid.NewGuid():N}.log");
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SerilogStepUp:EnableOtlpExporter"] = "false",
                ["SerilogStepUp:AlwaysLogRequestSummary"] = "true",
                ["Serilog:Using:0"] = "Serilog.Sinks.File",
                ["Serilog:WriteTo:0:Name"] = "File",
                ["Serilog:WriteTo:0:Args:path"] = summaryFile,
                ["Serilog:WriteTo:0:Args:shared"] = "true",
                ["Serilog:WriteTo:0:Args:formatter"] = "Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact",
            });
            builder.AddStepUpLogging((_, lc) => lc.WriteTo.Sink(capture));

            await using (var app = builder.Build())
            {
                app.UseStepUpRequestLogging();
                app.MapGet("/ping", () => "pong");
                await app.StartAsync(TestContext.Current.CancellationToken);

                using var client = app.GetTestClient();
                var response = await client.GetAsync("/ping", TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Same(sentinel, Log.Logger);

                await app.StopAsync(TestContext.Current.CancellationToken);
            }

            Assert.Same(sentinel, Log.Logger);
            Assert.Contains(capture.Events, e =>
                e.MessageTemplate.Text.StartsWith("HTTP ", StringComparison.Ordinal)
                && e.Properties.TryGetValue("RequestPath", out var path)
                && path is ScalarValue { Value: "/ping" });
            Assert.Contains(File.ReadAllLines(summaryFile), line =>
                line.Contains("\"IsRequestSummary\":true", StringComparison.Ordinal)
                && line.Contains("/ping", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(summaryFile)) File.Delete(summaryFile);
        }
    }
}
