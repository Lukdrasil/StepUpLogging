using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// End-to-end guards for the gated redaction enricher: on every route an event takes to an exporting sink the
/// secret never arrives unredacted, while a root <c>Serilog:Filter</c> sees a sub-floor event raw (ADR 0026 D5).
/// </summary>
public class RedactionShortcutHostTests
{
    private const string Secret = "hunter2";
    private const string SecretValue = "token=" + Secret;
    private const string Template = "{Marker} {Secret}";

    [Fact]
    public void ExportedWarning_SecretNeverReachesOutput()
    {
        var counts = Run([], configure: null, services =>
            Logger(services).Warning(Template, "SHORTCUT_WARN", SecretValue), "SHORTCUT_WARN");

        AssertExportedRedacted(counts, "SHORTCUT_WARN");
    }

    [Fact]
    public void HeldInformationFlushedByError_SecretNeverReachesOutput()
    {
        var counts = Run([], configure: null, services =>
        {
            Logger(services).Information(Template, "SHORTCUT_HELD_INFO", SecretValue);
            Logger(services).Error(Template, "SHORTCUT_HELD_ERR", SecretValue);
        }, "SHORTCUT_HELD_INFO", "SHORTCUT_HELD_ERR");

        AssertExportedRedacted(counts, "SHORTCUT_HELD_INFO");
        AssertExportedRedacted(counts, "SHORTCUT_HELD_ERR");
    }

    [Fact]
    public void ImmediateDebug_SecretNeverReachesOutput()
    {
        var counts = Run([], configure: null, services =>
            services.GetRequiredService<ILoggerFactory>().CreateLogger("Test.Shortcut")
                .LogImmediate(LogLevel.Debug, Template, "SHORTCUT_IMMEDIATE_DEBUG", SecretValue), "SHORTCUT_IMMEDIATE_DEBUG");

        AssertExportedRedacted(counts, "SHORTCUT_IMMEDIATE_DEBUG");
    }

    [Fact]
    public void RequestSummaryDebug_SecretNeverReachesOutput()
    {
        var counts = Run([], configure: null, services =>
            Logger(services).ForContext(LogProperties.IsRequestSummary, true)
                .Debug(Template, "SHORTCUT_SUMMARY_DEBUG", SecretValue), "SHORTCUT_SUMMARY_DEBUG");

        AssertExportedRedacted(counts, "SHORTCUT_SUMMARY_DEBUG");
    }

    [Fact]
    public void DebugAfterSwitchLoweredToDebug_SecretNeverReachesOutput()
    {
        var counts = Run([], configure: null, services =>
        {
            services.GetRequiredService<StepUpLoggingController>().LevelSwitch.MinimumLevel = LogEventLevel.Debug;
            Logger(services).Debug(Template, "SHORTCUT_LOWERED_DEBUG", SecretValue);
        }, "SHORTCUT_LOWERED_DEBUG");

        AssertExportedRedacted(counts, "SHORTCUT_LOWERED_DEBUG");
    }

    [Fact]
    public void ConfigureHookSinkDebug_SecretNeverReachesCapture()
    {
        var hookKey = Guid.NewGuid().ToString("N");
        try
        {
            Run([], (_, lc) => lc.WriteTo.StepUpCapture(hookKey), services =>
                Logger(services).Debug(Template, "SHORTCUT_HOOK_DEBUG", SecretValue));

            AssertCapturedRedacted(hookKey, "SHORTCUT_HOOK_DEBUG");
        }
        finally
        {
            KeyedCaptureSink.Forget(hookKey);
        }
    }

    [Fact]
    public void AuditToSinkDebug_SecretNeverReachesCapture()
    {
        var auditKey = Guid.NewGuid().ToString("N");
        try
        {
            Run(
                new()
                {
                    ["Serilog:AuditTo:0:Name"] = nameof(KeyedCaptureSink.StepUpAuditCapture),
                    ["Serilog:AuditTo:0:Args:key"] = auditKey,
                },
                configure: null,
                services => Logger(services).Debug(Template, "SHORTCUT_AUDITTO_DEBUG", SecretValue));

            AssertCapturedRedacted(auditKey, "SHORTCUT_AUDITTO_DEBUG");
        }
        finally
        {
            KeyedCaptureSink.Forget(auditKey);
        }
    }

    [Fact]
    public void RootFilterProbe_SubFloorDebug_SeesRawSecret_NotExported()
    {
        var probeKey = Guid.NewGuid().ToString("N");
        try
        {
            var counts = Run(
                new()
                {
                    ["Serilog:Filter:0:Name"] = nameof(KeyedCaptureSink.RootProbe),
                    ["Serilog:Filter:0:Args:key"] = probeKey,
                },
                configure: null,
                services => Logger(services).Debug(Template, "SHORTCUT_PROBE_DEBUG", SecretValue),
                "SHORTCUT_PROBE_DEBUG");

            Assert.Equal(SecretValue, SecretOf(WithMarker(KeyedCaptureSink.Events(probeKey), "SHORTCUT_PROBE_DEBUG")));
            Assert.Equal(0, counts["SHORTCUT_PROBE_DEBUG"]);
            Assert.Equal(0, counts[Secret]);
        }
        finally
        {
            KeyedCaptureSink.Forget(probeKey);
        }
    }

    /// <summary>Asserts the output file holds the event carrying <paramref name="marker"/> once and the secret nowhere.</summary>
    private static void AssertExportedRedacted(Dictionary<string, int> counts, string marker)
    {
        Assert.Equal(1, counts[marker]);
        Assert.Equal(0, counts[Secret]);
    }

    /// <summary>Asserts the capture under <paramref name="key"/> holds the event carrying <paramref name="marker"/> once, redacted.</summary>
    private static void AssertCapturedRedacted(string key, string marker)
    {
        Assert.Equal("[REDACTED]", SecretOf(WithMarker(KeyedCaptureSink.Events(key), marker)));
        Assert.Equal(0, KeyedCaptureSink.Count(key, Secret));
    }

    /// <summary>The value of the event's <c>Secret</c> property.</summary>
    private static object? SecretOf(LogEvent logEvent) => ((ScalarValue)logEvent.Properties["Secret"]).Value;

    /// <summary>The single event whose rendered message contains <paramref name="marker"/>.</summary>
    private static LogEvent WithMarker(LogEvent[] events, string marker)
        => Assert.Single(events, e => e.RenderMessage().Contains(marker, StringComparison.Ordinal));

    /// <summary>The host's Serilog logger.</summary>
    private static Serilog.ILogger Logger(IServiceProvider services) => services.GetRequiredService<Serilog.ILogger>();

    /// <summary>
    /// Runs <paramref name="scenario"/> through <see cref="TestHosts.RunInOneTrace"/> with property redaction on and
    /// returns how many output lines carry each marker and the secret.
    /// </summary>
    private static Dictionary<string, int> Run(
        Dictionary<string, string?> overrides,
        Action<IServiceProvider, LoggerConfiguration>? configure,
        Action<IServiceProvider> scenario,
        params string[] markers)
    {
        var settings = new Dictionary<string, string?>
        {
            ["SerilogStepUp:RedactLogEventProperties"] = "true",
            ["SerilogStepUp:RedactionRegexes:0"] = "token=[^&]+",
            ["Logging:LogLevel:Default"] = "Trace",
            ["Serilog:Using:0"] = typeof(KeyedCaptureSink).Assembly.GetName().Name,
        };
        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        return TestHosts.RunInOneTrace(settings, configure, scenario, [.. markers, Secret]);
    }
}
