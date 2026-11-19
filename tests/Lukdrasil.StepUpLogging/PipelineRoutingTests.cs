using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Parsing;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// End-to-end routing tests over the root logger <see cref="StepUpPipeline"/> builds, with one config-declared
/// capture sink that both output loggers (gated and bypass) write to, so every assertion counts what is exported:
///
/// Root (Verbose)
///  ├─ StepUpSink  gated by LevelSwitch, drops IsRequestSummary=true and IsImmediate=true, holds the rest
///  ├─ PreErrorBufferSink  per-trace ring buffer floored at StepUpLevel, flushes to bypass on Error
///  ├─ StepUpTriggerSink  fires controller.Trigger() on Error/Fatal (regardless of marker)
///  ├─ BypassSink.RequestSummaries  forwards IsRequestSummary=true to bypass
///  └─ BypassSink.Immediate  forwards IsImmediate=true to bypass
/// </summary>
public class PipelineRoutingTests
{
    // ─── helpers ──────────────────────────────────────────────────────────────────

    private const string Routed = "ROUTED_EVENT";

    private sealed class Pipeline : IDisposable
    {
        private readonly string _captureKey = Guid.NewGuid().ToString("N");
        private readonly Logger _root;

        public Serilog.ILogger Root => _root;
        public StepUpLoggingController Controller { get; }

        public Pipeline(string baseLevel = "Warning", string stepUpLevel = "Information", bool withPreErrorBuffer = false)
        {
            var builder = TestHosts.NewBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:Using:0"] = typeof(KeyedCaptureSink).Assembly.GetName().Name,
                ["Serilog:WriteTo:0:Name"] = nameof(KeyedCaptureSink.StepUpCapture),
                ["Serilog:WriteTo:0:Args:key"] = _captureKey,
            });

            var opts = new StepUpLoggingOptions
            {
                Mode = StepUpMode.Auto,
                BaseLevel = baseLevel,
                StepUpLevel = stepUpLevel,
                DurationSeconds = 10,
                EnablePreErrorBuffering = withPreErrorBuffer,
                EnableOtlpExporter = false,
            };

            Controller = new StepUpLoggingController(opts);

            var lc = new LoggerConfiguration();
            new StepUpPipeline(builder, new StepUpSettings(opts), new CompiledRedactionPatterns([]), null)
                .Configure(lc, Controller, new ServiceCollection().BuildServiceProvider(), configure: null, builder.Configuration.GetSection("SerilogStepUp"));
            _root = lc.CreateLogger();
        }

        /// <summary>The events built by <see cref="MakeEvent"/> that reached an output, in export order.</summary>
        public LogEvent[] Exported => [.. KeyedCaptureSink.Events(_captureKey).Where(e => e.MessageTemplate.Text == Routed)];

        public int Count(string token) => KeyedCaptureSink.Count(_captureKey, token);

        public void Dispose()
        {
            _root.Dispose();
            Controller.Dispose();
            KeyedCaptureSink.Forget(_captureKey);
        }
    }

    private static LogEvent MakeEvent(LogEventLevel level, params LogEventProperty[] properties)
    {
        var parser = new MessageTemplateParser();
        return new LogEvent(DateTimeOffset.UtcNow, level, null, parser.Parse(Routed), properties);
    }

    private static LogEventProperty Immediate() =>
        new(LogProperties.IsImmediate, new ScalarValue(true));

    private static LogEventProperty Summary() =>
        new(LogProperties.IsRequestSummary, new ScalarValue(true));

    private static LogEventLevel[] Levels(Pipeline p) => [.. p.Exported.Select(e => e.Level)];

    // ─── 1. Normal event routing ───────────────────────────────────────────────

    [Fact]
    public void NormalWarning_WithBaseLevelWarning_IsExportedOnce()
    {
        using var p = new Pipeline();

        p.Root.Write(MakeEvent(LogEventLevel.Warning));

        Assert.Single(p.Exported);
    }

    [Fact]
    public void NormalInformation_WithBaseLevelWarning_IsNotExported()
    {
        using var p = new Pipeline();

        p.Root.Write(MakeEvent(LogEventLevel.Information));

        Assert.Empty(p.Exported);
    }

    [Fact]
    public void NormalInformation_WhenSteppedUp_IsExportedOnce()
    {
        using var p = new Pipeline();

        p.Controller.Trigger(); // raises LevelSwitch to Information
        p.Root.Write(MakeEvent(LogEventLevel.Information));

        Assert.Single(p.Exported);
    }

    [Fact]
    public void NormalError_IsExportedOnceAndTriggersStepUp()
    {
        using var p = new Pipeline();

        Assert.False(p.Controller.IsSteppedUp);

        p.Root.Write(MakeEvent(LogEventLevel.Error));

        Assert.True(SpinWait.SpinUntil(() => p.Controller.IsSteppedUp, 2000));
        Assert.Single(p.Exported);
    }

    // ─── 2. Immediate event routing ───────────────────────────────────────────

    [Fact]
    public void ImmediateInformation_WithBaseLevelWarning_IsExportedOnce()
    {
        using var p = new Pipeline();

        p.Root.Write(MakeEvent(LogEventLevel.Information, Immediate()));

        Assert.Single(p.Exported);
    }

    [Fact]
    public void ImmediateWarning_WithBaseLevelWarning_IsExportedOnce_NotDuplicated()
    {
        using var p = new Pipeline();

        // StepUpSink must drop it despite Warning >= Warning base level, or it is exported twice.
        p.Root.Write(MakeEvent(LogEventLevel.Warning, Immediate()));

        Assert.Single(p.Exported);
    }

    [Fact]
    public void ImmediateError_IsExportedOnceAndTriggersStepUp()
    {
        using var p = new Pipeline();

        Assert.False(p.Controller.IsSteppedUp);

        p.Root.Write(MakeEvent(LogEventLevel.Error, Immediate()));

        // Trigger still fires — StepUpTriggerSink is not filtered
        Assert.True(SpinWait.SpinUntil(() => p.Controller.IsSteppedUp, 2000));
        Assert.Single(p.Exported);
    }

    [Fact]
    public void ImmediateInformation_WhenSteppedUp_IsExportedOnce_NoDuplication()
    {
        using var p = new Pipeline();

        p.Controller.Trigger(); // LevelSwitch now at Information
        Assert.True(p.Controller.IsSteppedUp);

        // Even though LevelSwitch is at Information, StepUpSink must still drop IsImmediate events.
        p.Root.Write(MakeEvent(LogEventLevel.Information, Immediate()));

        Assert.Single(p.Exported);
    }

    [Fact]
    public void MultipleImmediateLogs_ExactCount_NoDuplication()
    {
        using var p = new Pipeline();

        const int count = 5;
        for (var i = 0; i < count; i++)
        {
            p.Root.Write(MakeEvent(LogEventLevel.Information, Immediate()));
        }

        Assert.Equal(count, p.Exported.Length);
    }

    [Fact]
    public void MixedEvents_OnlyTheWarningAndTheImmediateAreExported()
    {
        using var p = new Pipeline();

        p.Root.Write(MakeEvent(LogEventLevel.Warning));                        // → step-up
        p.Root.Write(MakeEvent(LogEventLevel.Information, Immediate()));        // → bypass
        p.Root.Write(MakeEvent(LogEventLevel.Information));                     // → nowhere

        var exported = p.Exported;
        Assert.Equal(2, exported.Length);
        Assert.Equal(LogEventLevel.Warning, exported[0].Level);
        Assert.True(LogProperties.HasFlag(exported[1], LogProperties.IsImmediate));
    }

    // ─── 3. Request summary routing ───────────────────────────────────────────

    [Fact]
    public void RequestSummary_IsExportedOnce()
    {
        using var p = new Pipeline();

        p.Root.Write(MakeEvent(LogEventLevel.Information, Summary()));

        Assert.Single(p.Exported);
    }

    [Fact]
    public void RequestSummary_WithSwitchAtVerbose_IsExportedOnce()
    {
        using var p = new Pipeline();

        // Even when LevelSwitch is at Verbose, StepUpSink must not export the summary a second time.
        p.Controller.LevelSwitch.MinimumLevel = LogEventLevel.Verbose;
        p.Root.Write(MakeEvent(LogEventLevel.Information, Summary()));

        Assert.Single(p.Exported);
    }

    // ─── 4. Pre-error buffer interaction ──────────────────────────────────────

    [Fact]
    public void PreErrorBuffer_FlushesHeldEventsAtStepUpLevelOnError_NotDebug()
    {
        using var p = new Pipeline(withPreErrorBuffer: true);

        // Information sits between StepUpLevel and BaseLevel: held. Debug is below StepUpLevel: never held.
        p.Root.Write(MakeEvent(LogEventLevel.Information));
        p.Root.Write(MakeEvent(LogEventLevel.Information));
        p.Root.Write(MakeEvent(LogEventLevel.Debug));
        Assert.Empty(p.Exported); // still buffered

        p.Root.Write(MakeEvent(LogEventLevel.Error));

        Assert.Equal([LogEventLevel.Error, LogEventLevel.Information, LogEventLevel.Information], Levels(p));
    }

    [Fact]
    public void PreErrorBuffer_ExportedWarning_IsNotFlushedAgainOnError()
    {
        using var p = new Pipeline(withPreErrorBuffer: true);

        p.Root.Write(MakeEvent(LogEventLevel.Warning));
        p.Root.Write(MakeEvent(LogEventLevel.Information));
        p.Root.Write(MakeEvent(LogEventLevel.Error));

        Assert.Equal([LogEventLevel.Warning, LogEventLevel.Error, LogEventLevel.Information], Levels(p));
    }

    [Fact]
    public void ImmediateLog_WithBufferingEnabled_IsExportedBeforeAnyError_AndNotFlushedAgain()
    {
        using var p = new Pipeline(withPreErrorBuffer: true);

        p.Root.Write(MakeEvent(LogEventLevel.Information, Immediate()));
        Assert.Single(p.Exported);

        p.Root.Write(MakeEvent(LogEventLevel.Error));

        Assert.Equal([LogEventLevel.Information, LogEventLevel.Error], Levels(p));
    }

    // ─── 5. Level-switch boundary conditions ──────────────────────────────────

    [Fact]
    public void ImmediateDebug_WithBaseLevelWarning_IsExportedOnce()
    {
        // The LevelSwitch gate must not prevent Debug-level immediate events from exporting.
        using var p = new Pipeline();

        p.Root.Write(MakeEvent(LogEventLevel.Debug, Immediate()));

        Assert.Single(p.Exported);
    }

    [Fact]
    public void ImmediateVerbose_WithBaseLevelWarning_IsExportedOnce()
    {
        using var p = new Pipeline();

        p.Root.Write(MakeEvent(LogEventLevel.Verbose, Immediate()));

        Assert.Single(p.Exported);
    }

    [Fact]
    public void LevelSwitchGate_ExactBoundary()
    {
        // At Warning base level: Warning passes, Information does not.
        using var p = new Pipeline(baseLevel: "Warning");

        p.Root.Write(MakeEvent(LogEventLevel.Information));
        p.Root.Write(MakeEvent(LogEventLevel.Warning));
        p.Root.Write(MakeEvent(LogEventLevel.Error));

        Assert.Equal([LogEventLevel.Warning, LogEventLevel.Error], Levels(p));
    }

    [Fact]
    public void StepUpImmediateAndSummaryEvents_EachExportedOnce()
    {
        using var p = new Pipeline();

        // 3 normal (at/above level) → step-up; 3 immediate → bypass; 2 summary → bypass
        for (var i = 0; i < 3; i++) p.Root.Write(MakeEvent(LogEventLevel.Warning));
        for (var i = 0; i < 3; i++) p.Root.Write(MakeEvent(LogEventLevel.Warning, Immediate()));
        for (var i = 0; i < 2; i++) p.Root.Write(MakeEvent(LogEventLevel.Information, Summary()));

        Assert.Equal(8, p.Exported.Length);
    }

    // ─── 6. MEL (ILogger) end-to-end: BeginImmediateScope routing ─────────────

    [Fact]
    public void MelScope_ImmediateScope_IsExportedBelowTheGate()
    {
        using var p = new Pipeline();
        using var factory = new SerilogLoggerFactory(p.Root, dispose: false);
        var logger = factory.CreateLogger("RoutingTest");

        using (logger.BeginImmediateScope())
        {
            logger.LogInformation("MEL_SCOPED_ONE");
            logger.LogInformation("MEL_SCOPED_TWO");
        }

        logger.LogWarning("MEL_NORMAL_WARNING");

        Assert.Equal(1, p.Count("MEL_SCOPED_ONE"));
        Assert.Equal(1, p.Count("MEL_SCOPED_TWO"));
        Assert.Equal(1, p.Count("MEL_NORMAL_WARNING"));
    }

    [Fact]
    public void MelScope_ImmediateScope_ScopeEndsCleanly()
    {
        using var p = new Pipeline();
        using var factory = new SerilogLoggerFactory(p.Root, dispose: false);
        var logger = factory.CreateLogger("RoutingTest");

        using (logger.BeginImmediateScope())
        {
            logger.LogInformation("MEL_INSIDE");
        }

        // After scope ends, Information is back to normal routing (below Warning base level → nowhere)
        logger.LogInformation("MEL_OUTSIDE");

        Assert.Equal(1, p.Count("MEL_INSIDE"));
        Assert.Equal(0, p.Count("MEL_OUTSIDE"));
    }

    [Fact]
    public void MelHelper_LogImmediateError_IsExportedOnceAndTriggersStepUp()
    {
        using var p = new Pipeline();
        using var factory = new SerilogLoggerFactory(p.Root, dispose: false);
        var logger = factory.CreateLogger("RoutingTest");

        Assert.False(p.Controller.IsSteppedUp);

        logger.LogImmediateError("MEL_IMMEDIATE_ERROR");

        // Trigger still fired despite the IsImmediate marker
        Assert.True(SpinWait.SpinUntil(() => p.Controller.IsSteppedUp, 2000));
        Assert.Equal(1, p.Count("MEL_IMMEDIATE_ERROR"));
    }

    // ─── 7. LogProperties marker correctness ──────────────────────────────────

    [Fact]
    public void ImmediateEvent_HasIsImmediateProperty_WhenExported()
    {
        using var p = new Pipeline();

        p.Root.Write(MakeEvent(LogEventLevel.Information, Immediate()));

        var evt = Assert.Single(p.Exported);
        Assert.True(evt.Properties.TryGetValue(LogProperties.IsImmediate, out var val));
        Assert.IsType<ScalarValue>(val);
        Assert.Equal(true, ((ScalarValue)val!).Value);
    }

    [Fact]
    public void SummaryEvent_HasIsRequestSummaryProperty_WhenExported()
    {
        using var p = new Pipeline();

        p.Root.Write(MakeEvent(LogEventLevel.Information, Summary()));

        var evt = Assert.Single(p.Exported);
        Assert.True(evt.Properties.TryGetValue(LogProperties.IsRequestSummary, out var val));
        Assert.Equal(true, ((ScalarValue)val!).Value);
    }

    // ─── 8. NeverStepUpCategories deny-list wiring (ADR 0021) ──────────────────

    private const string EfCategory = "Microsoft.EntityFrameworkCore.Database.Command";

    [Fact]
    public void AlwaysOn_DefaultDenyList_EfInformation_StillReachesOutput()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"stepup-b03-alwayson-{Guid.NewGuid():N}.log");
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SerilogStepUp:EnableOtlpExporter"] = "false",
                ["SerilogStepUp:EnablePreErrorBuffering"] = "false",
                ["SerilogStepUp:Mode"] = "AlwaysOn",
                ["SerilogStepUp:BaseLevel"] = "Warning",
                ["SerilogStepUp:StepUpLevel"] = "Information",
                ["Serilog:Using:0"] = "Serilog.Sinks.File",
                ["Serilog:WriteTo:0:Name"] = "File",
                ["Serilog:WriteTo:0:Args:path"] = tempFile,
                // shared:true is required once a config File sink attaches to both the gated and the
                // bypass logger, or the two writers contend for the file lock (ADR 0003).
                ["Serilog:WriteTo:0:Args:shared"] = "true",
            });
            builder.AddStepUpLogging();

            const string token = "ALWAYSON_EF_TOKEN_b03";
            using (var host = builder.Build())
            {
                var logger = host.Services.GetRequiredService<Serilog.ILogger>();
                logger.ForContext("SourceContext", EfCategory).Information(token);
            }

            Assert.Contains(token, File.ReadAllText(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Auto_SteppedUp_DefaultDenyList_EfInformation_IsDropped()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"stepup-b03-auto-{Guid.NewGuid():N}.log");
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SerilogStepUp:EnableOtlpExporter"] = "false",
                ["SerilogStepUp:EnablePreErrorBuffering"] = "false",
                ["SerilogStepUp:Mode"] = "Auto",
                ["SerilogStepUp:BaseLevel"] = "Warning",
                ["SerilogStepUp:StepUpLevel"] = "Information",
                ["Serilog:Using:0"] = "Serilog.Sinks.File",
                ["Serilog:WriteTo:0:Name"] = "File",
                ["Serilog:WriteTo:0:Args:path"] = tempFile,
                // shared:true is required once a config File sink attaches to both the gated and the
                // bypass logger, or the two writers contend for the file lock (ADR 0003).
                ["Serilog:WriteTo:0:Args:shared"] = "true",
            });
            builder.AddStepUpLogging();

            const string efToken = "AUTO_EF_TOKEN_b03";
            const string appToken = "AUTO_APP_TOKEN_b03";
            using (var host = builder.Build())
            {
                var logger = host.Services.GetRequiredService<Serilog.ILogger>();
                var controller = host.Services.GetRequiredService<StepUpLoggingController>();

                controller.Trigger();
                SpinWait.SpinUntil(() => controller.LevelSwitch.MinimumLevel <= LogEventLevel.Information, 2000);

                logger.ForContext("SourceContext", EfCategory).Information(efToken);
                logger.ForContext("SourceContext", "MyApp.Widget").Information(appToken);
            }

            var contents = File.ReadAllText(tempFile);
            Assert.DoesNotContain(efToken, contents);
            Assert.Contains(appToken, contents);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Auto_SteppedUp_BlankEntry_DoesNotSilenceOtherCategories()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"stepup-b03-blank-{Guid.NewGuid():N}.log");
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SerilogStepUp:EnableOtlpExporter"] = "false",
                ["SerilogStepUp:EnablePreErrorBuffering"] = "false",
                ["SerilogStepUp:Mode"] = "Auto",
                ["SerilogStepUp:BaseLevel"] = "Warning",
                ["SerilogStepUp:StepUpLevel"] = "Information",
                ["SerilogStepUp:NeverStepUpCategories:0"] = "",
                ["SerilogStepUp:NeverStepUpCategories:1"] = "   ",
                ["SerilogStepUp:NeverStepUpCategories:2"] = EfCategory,
                ["Serilog:Using:0"] = "Serilog.Sinks.File",
                ["Serilog:WriteTo:0:Name"] = "File",
                ["Serilog:WriteTo:0:Args:path"] = tempFile,
                // shared:true is required once a config File sink attaches to both the gated and the
                // bypass logger, or the two writers contend for the file lock (ADR 0003).
                ["Serilog:WriteTo:0:Args:shared"] = "true",
            });
            builder.AddStepUpLogging();

            const string efToken = "BLANK_EF_TOKEN_b03";
            const string rootedToken = "BLANK_ROOTED_TOKEN_b03";
            using (var host = builder.Build())
            {
                var logger = host.Services.GetRequiredService<Serilog.ILogger>();
                var controller = host.Services.GetRequiredService<StepUpLoggingController>();

                controller.Trigger();
                SpinWait.SpinUntil(() => controller.LevelSwitch.MinimumLevel <= LogEventLevel.Information, 2000);

                logger.ForContext("SourceContext", EfCategory).Information(efToken);
                // ".Weird" is dot-rooted: an UNfiltered empty prefix matches it, so this is the only
                // context the blank filter observably changes. It must still export.
                logger.ForContext("SourceContext", ".Weird").Information(rootedToken);
            }

            var contents = File.ReadAllText(tempFile);
            // Blank entries are filtered, so a dot-rooted context is not pinned to BaseLevel.
            Assert.Contains(rootedToken, contents);
            // The real entry still works alongside the (filtered) blanks.
            Assert.DoesNotContain(efToken, contents);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ─── 9. NeverTriggerCategories wiring ──────────────────────────────────────

    private static string RunNeverTriggerHost(Dictionary<string, string?> settings, Action<Serilog.ILogger> log, int waitMs, out bool steppedUp)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"stepup-nevertrigger-{Guid.NewGuid():N}.log");
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(settings)
            {
                ["SerilogStepUp:EnableOtlpExporter"] = "false",
                ["SerilogStepUp:EnablePreErrorBuffering"] = "true",
                ["SerilogStepUp:Mode"] = "Auto",
                ["SerilogStepUp:BaseLevel"] = "Warning",
                ["SerilogStepUp:StepUpLevel"] = "Information",
                ["Serilog:Using:0"] = "Serilog.Sinks.File",
                ["Serilog:WriteTo:0:Name"] = "File",
                ["Serilog:WriteTo:0:Args:path"] = tempFile,
                ["Serilog:WriteTo:0:Args:shared"] = "true",
            });
            builder.AddStepUpLogging();

            using (var host = builder.Build())
            {
                var controller = host.Services.GetRequiredService<StepUpLoggingController>();
                log(host.Services.GetRequiredService<Serilog.ILogger>());
                SpinWait.SpinUntil(() => controller.IsSteppedUp, waitMs);
                steppedUp = controller.IsSteppedUp;
            }

            return File.ReadAllText(tempFile);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Auto_NeverTriggerCategoryError_DoesNotStepUp_ExportsAndFlushesTraceBuffer()
    {
        const string heldToken = "NEVERTRIGGER_HELD_TOKEN";
        const string errorToken = "NEVERTRIGGER_ERROR_TOKEN";

        var contents = RunNeverTriggerHost(
            new() { ["SerilogStepUp:NeverTriggerCategories:0"] = "Polly" },
            logger =>
            {
                using var activity = new System.Diagnostics.Activity("never-trigger").Start();
                logger.ForContext("SourceContext", "MyApp.Widget").Information(heldToken);
                logger.ForContext("SourceContext", "Polly.Retry").Error(errorToken);
            },
            waitMs: 500,
            out var steppedUp);

        Assert.False(steppedUp);
        Assert.Contains(errorToken, contents);
        Assert.Contains(heldToken, contents);
    }

    [Fact]
    public void Auto_NeverTriggerBlankEntry_DoesNotSuppressDotRootedError()
    {
        RunNeverTriggerHost(
            new()
            {
                ["SerilogStepUp:NeverTriggerCategories:0"] = "",
                ["SerilogStepUp:NeverTriggerCategories:1"] = "Polly",
            },
            logger => logger.ForContext("SourceContext", ".Weird").Error("rooted error"),
            waitMs: 2000,
            out var steppedUp);

        Assert.True(steppedUp);
    }

    // ─── 10. Pre-error buffer floor with the enrichment gate open ─────────────

    [Fact]
    public void PreErrorBuffer_WithConfigureHook_FlushesOnlyEventsAtOrAboveStepUpLevel()
    {
        // The configure hook opens the enrichment gate (ADR 0026), so a Debug event reaches the buffer and only the
        // buffer's StepUpLevel floor keeps it out of the flush.
        var counts = TestHosts.RunInOneTrace(
            [],
            (_, _) => { },
            services =>
            {
                var logger = services.GetRequiredService<Serilog.ILogger>();
                logger.Debug("{Token}", "GATE_OPEN_DEBUG");
                logger.Information("{Token}", "GATE_OPEN_INFO");
                logger.Error("{Token}", "GATE_OPEN_ERROR");
            },
            "GATE_OPEN_DEBUG", "GATE_OPEN_INFO", "GATE_OPEN_ERROR");

        Assert.Equal(0, counts["GATE_OPEN_DEBUG"]);
        Assert.Equal(1, counts["GATE_OPEN_INFO"]);
        Assert.Equal(1, counts["GATE_OPEN_ERROR"]);
    }
}
