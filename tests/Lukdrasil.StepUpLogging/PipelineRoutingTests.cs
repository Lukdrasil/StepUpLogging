using System;
using System.Collections.Generic;
using System.IO;
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
/// End-to-end routing tests that wire the full Serilog pipeline and verify the routing invariants
/// documented in the plan:
///
/// Root (Verbose)
///  ├─ StepUpSink  gated by LevelSwitch, drops IsRequestSummary=true and IsImmediate=true
///  ├─ PreErrorBufferSink  per-trace ring buffer, flushes to bypass on Error
///  ├─ StepUpTriggerSink  fires controller.Trigger() on Error/Fatal (regardless of marker)
///  ├─ SummarySink  forwards IsRequestSummary=true to bypass
///  └─ ImmediateSink  forwards IsImmediate=true to bypass
/// </summary>
public class PipelineRoutingTests
{
    // ─── helpers ──────────────────────────────────────────────────────────────────

    private sealed class Pipeline : IDisposable
    {
        public Serilog.ILogger Root { get; }
        public CollectingSink StepUpOutput { get; } = new();
        public CollectingSink BypassOutput { get; } = new();
        public StepUpLoggingController Controller { get; }

        private readonly StepUpSink _stepUpSink;
        private readonly StepUpTriggerSink _triggerSink;

        public Pipeline(string baseLevel = "Warning", string stepUpLevel = "Information", bool withPreErrorBuffer = false)
        {
            var opts = new StepUpLoggingOptions
            {
                Mode = StepUpMode.Auto,
                BaseLevel = baseLevel,
                StepUpLevel = stepUpLevel,
                DurationSeconds = 10,
                EnablePreErrorBuffering = withPreErrorBuffer
            };

            Controller = new StepUpLoggingController(opts);

            var stepUpInner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(StepUpOutput).CreateLogger();
            var bypassLogger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(BypassOutput).CreateLogger();

            _stepUpSink = new StepUpSink(stepUpInner, Controller.LevelSwitch, Controller.BaseLevel, []);
            _triggerSink = new StepUpTriggerSink(Controller);

            var cfg = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Sink(_stepUpSink)
                .WriteTo.Sink(_triggerSink)
                .WriteTo.Sink(new SummarySink(bypassLogger))
                .WriteTo.Sink(new ImmediateSink(bypassLogger));

            if (withPreErrorBuffer)
            {
                cfg.WriteTo.Sink(new PreErrorBufferSink(bypassLogger, 50, 64));
            }

            Root = cfg.CreateLogger();
        }

        public void Dispose()
        {
            _triggerSink.Dispose();
            _stepUpSink.Dispose();
            Controller.Dispose();
        }
    }

    private static LogEvent MakeEvent(LogEventLevel level, params LogEventProperty[] properties)
    {
        var parser = new MessageTemplateParser();
        return new LogEvent(DateTimeOffset.UtcNow, level, null, parser.Parse("msg"), properties);
    }

    private static LogEventProperty Immediate() =>
        new(LogProperties.IsImmediate, new ScalarValue(true));

    private static LogEventProperty Summary() =>
        new(LogProperties.IsRequestSummary, new ScalarValue(true));

    // ─── 1. Normal event routing ───────────────────────────────────────────────

    [Fact]
    public void NormalWarning_WithBaseLevelWarning_GoesToStepUpOnly()
    {
        using var p = new Pipeline();

        p.Root.Write(MakeEvent(LogEventLevel.Warning));

        Assert.Single(p.StepUpOutput.Events);
        Assert.Empty(p.BypassOutput.Events);
    }

    [Fact]
    public void NormalInformation_WithBaseLevelWarning_GoesNowhere()
    {
        using var p = new Pipeline();

        p.Root.Write(MakeEvent(LogEventLevel.Information));

        Assert.Empty(p.StepUpOutput.Events);
        Assert.Empty(p.BypassOutput.Events);
    }

    [Fact]
    public void NormalInformation_WhenSteppedUp_GoesToStepUpOnly()
    {
        using var p = new Pipeline();

        p.Controller.Trigger(); // raises LevelSwitch to Information
        p.Root.Write(MakeEvent(LogEventLevel.Information));

        Assert.Single(p.StepUpOutput.Events);
        Assert.Empty(p.BypassOutput.Events);
    }

    [Fact]
    public async Task NormalError_GoesToStepUpAndTriggersStepUp()
    {
        using var p = new Pipeline();

        Assert.False(p.Controller.IsSteppedUp);

        p.Root.Write(MakeEvent(LogEventLevel.Error));
        await Task.Delay(150); // let trigger channel process

        // Error goes through StepUpSink (Error >= Warning base level)
        Assert.Single(p.StepUpOutput.Events);
        // Trigger fired → controller stepped up
        Assert.True(p.Controller.IsSteppedUp);
        // Bypass is empty (error has no bypass marker)
        Assert.Empty(p.BypassOutput.Events);
    }

    // ─── 2. Immediate event routing ───────────────────────────────────────────

    [Fact]
    public void ImmediateInformation_WithBaseLevelWarning_GoesToBypassOnly()
    {
        using var p = new Pipeline();

        p.Root.Write(MakeEvent(LogEventLevel.Information, Immediate()));

        Assert.Empty(p.StepUpOutput.Events);
        Assert.Single(p.BypassOutput.Events);
    }

    [Fact]
    public void ImmediateWarning_WithBaseLevelWarning_GoesToBypassOnly_NotDuplicated()
    {
        using var p = new Pipeline();

        p.Root.Write(MakeEvent(LogEventLevel.Warning, Immediate()));

        // StepUpSink must have dropped it despite Warning >= Warning base level
        Assert.Empty(p.StepUpOutput.Events);
        Assert.Single(p.BypassOutput.Events);
    }

    [Fact]
    public async Task ImmediateError_GoesToBypassAndTriggersStepUp()
    {
        using var p = new Pipeline();

        Assert.False(p.Controller.IsSteppedUp);

        p.Root.Write(MakeEvent(LogEventLevel.Error, Immediate()));
        await Task.Delay(150);

        // Bypass receives it (ImmediateSink picks it up)
        Assert.Single(p.BypassOutput.Events);
        // StepUpSink dropped it (IsImmediate=true)
        Assert.Empty(p.StepUpOutput.Events);
        // Trigger still fires — StepUpTriggerSink is not filtered
        Assert.True(p.Controller.IsSteppedUp);
    }

    [Fact]
    public void ImmediateInformation_WhenSteppedUp_GoesToBypassOnly_NoDuplication()
    {
        using var p = new Pipeline();

        p.Controller.Trigger(); // LevelSwitch now at Information
        Assert.True(p.Controller.IsSteppedUp);

        p.Root.Write(MakeEvent(LogEventLevel.Information, Immediate()));

        // Even though LevelSwitch is at Information (step-up active),
        // StepUpSink must still drop IsImmediate events.
        Assert.Empty(p.StepUpOutput.Events);
        Assert.Single(p.BypassOutput.Events);
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

        Assert.Empty(p.StepUpOutput.Events);
        Assert.Equal(count, p.BypassOutput.Events.Count);
    }

    [Fact]
    public void MixedEvents_RoutedToCorrectSinks()
    {
        using var p = new Pipeline();

        p.Root.Write(MakeEvent(LogEventLevel.Warning));                        // → step-up
        p.Root.Write(MakeEvent(LogEventLevel.Information, Immediate()));        // → bypass
        p.Root.Write(MakeEvent(LogEventLevel.Information));                     // → nowhere

        Assert.Single(p.StepUpOutput.Events);
        Assert.Single(p.BypassOutput.Events);
    }

    // ─── 3. Request summary routing ───────────────────────────────────────────

    [Fact]
    public void RequestSummary_GoesToBypassViaSummarySink()
    {
        using var p = new Pipeline();

        p.Root.Write(MakeEvent(LogEventLevel.Information, Summary()));

        Assert.Single(p.BypassOutput.Events);
        Assert.Empty(p.StepUpOutput.Events);
    }

    [Fact]
    public void RequestSummary_IsNotPickedUpByImmediateSink()
    {
        // IsRequestSummary=true should only go to SummarySink, not ImmediateSink.
        // Since both route to the same BypassOutput in the test, we verify count stays at 1.
        using var p = new Pipeline();

        p.Root.Write(MakeEvent(LogEventLevel.Information, Summary()));

        // Exactly one write to bypass (SummarySink), not two (which would happen if ImmediateSink also picked it up).
        Assert.Equal(1, p.BypassOutput.Events.Count);
    }

    [Fact]
    public void RequestSummary_DoesNotAppearInStepUpOutput()
    {
        using var p = new Pipeline();

        // Even when LevelSwitch is at Verbose, summary events must not reach the step-up output
        p.Controller.LevelSwitch.MinimumLevel = LogEventLevel.Verbose;
        p.Root.Write(MakeEvent(LogEventLevel.Information, Summary()));

        Assert.Empty(p.StepUpOutput.Events);
    }

    [Fact]
    public void ImmediateLog_IsNotPickedUpBySummarySink()
    {
        // IsImmediate=true should only go to ImmediateSink, not SummarySink.
        // Verified by count staying at 1.
        using var p = new Pipeline();

        p.Root.Write(MakeEvent(LogEventLevel.Information, Immediate()));

        Assert.Equal(1, p.BypassOutput.Events.Count);
    }

    // ─── 4. Pre-error buffer interaction ──────────────────────────────────────

    [Fact]
    public void ImmediateLog_IsNotHeldInBuffer_AppearsImmediately()
    {
        // ImmediateSink runs before or alongside PreErrorBufferSink in the pipeline.
        // The immediate event should be in bypass regardless of whether an error occurs.
        using var p = new Pipeline(withPreErrorBuffer: false);

        p.Root.Write(MakeEvent(LogEventLevel.Information, Immediate()));

        Assert.Single(p.BypassOutput.Events);
    }

    [Fact]
    public void PreErrorBuffer_WithPreErrorBufferingEnabled_FlushesPreErrorLogsToBypassOnError()
    {
        using var p = new Pipeline(withPreErrorBuffer: true);

        // Emit some Debug events (buffered, not yet visible)
        p.Root.Write(MakeEvent(LogEventLevel.Debug));
        p.Root.Write(MakeEvent(LogEventLevel.Debug));
        Assert.Empty(p.BypassOutput.Events); // still buffered

        // Error triggers flush of the two Debug events to bypass
        p.Root.Write(MakeEvent(LogEventLevel.Error));

        Assert.Equal(2, p.BypassOutput.Events.Count);
    }

    [Fact]
    public void ImmediateLog_WithBufferingEnabled_StillBypassesBuffer()
    {
        // An IsImmediate log must reach bypass even when pre-error buffering is on,
        // before any error occurs.
        using var p = new Pipeline(withPreErrorBuffer: true);

        p.Root.Write(MakeEvent(LogEventLevel.Information, Immediate()));

        Assert.Single(p.BypassOutput.Events);
    }

    // ─── 5. Level-switch boundary conditions ──────────────────────────────────

    [Fact]
    public void ImmediateDebug_WithBaseLevelWarning_StillReachesBypass()
    {
        // The LevelSwitch gate must not prevent Debug-level immediate events from exporting.
        using var p = new Pipeline();

        p.Root.Write(MakeEvent(LogEventLevel.Debug, Immediate()));

        Assert.Empty(p.StepUpOutput.Events);
        Assert.Single(p.BypassOutput.Events);
    }

    [Fact]
    public void ImmediateVerbose_WithBaseLevelWarning_StillReachesBypass()
    {
        using var p = new Pipeline();

        p.Root.Write(MakeEvent(LogEventLevel.Verbose, Immediate()));

        Assert.Empty(p.StepUpOutput.Events);
        Assert.Single(p.BypassOutput.Events);
    }

    [Fact]
    public void StepUpSink_LevelSwitchGate_ExactBoundary()
    {
        // At Warning base level: Warning passes, Information does not.
        using var p = new Pipeline(baseLevel: "Warning");

        p.Root.Write(MakeEvent(LogEventLevel.Information));
        p.Root.Write(MakeEvent(LogEventLevel.Warning));
        p.Root.Write(MakeEvent(LogEventLevel.Error));

        Assert.Equal(2, p.StepUpOutput.Events.Count); // Warning + Error
        Assert.Empty(p.BypassOutput.Events);
    }

    [Fact]
    public void StepUpAndImmediateEvents_TotalCountAcrossBothSinks()
    {
        using var p = new Pipeline();

        // 3 normal (at/above level) → step-up; 3 immediate → bypass; 2 summary → bypass
        for (var i = 0; i < 3; i++) p.Root.Write(MakeEvent(LogEventLevel.Warning));
        for (var i = 0; i < 3; i++) p.Root.Write(MakeEvent(LogEventLevel.Warning, Immediate()));
        for (var i = 0; i < 2; i++) p.Root.Write(MakeEvent(LogEventLevel.Information, Summary()));

        Assert.Equal(3, p.StepUpOutput.Events.Count);
        Assert.Equal(5, p.BypassOutput.Events.Count); // 3 immediate + 2 summary
    }

    // ─── 6. MEL (ILogger) end-to-end: BeginImmediateScope routing ─────────────

    [Fact]
    public void MelScope_ImmediateScope_RoutesToBypass()
    {
        var stepUpOutput = new CollectingSink();
        var bypassOutput = new CollectingSink();

        var stepUpInner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(stepUpOutput).CreateLogger();
        var bypassLogger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(bypassOutput).CreateLogger();

        var opts = new StepUpLoggingOptions { BaseLevel = "Warning", StepUpLevel = "Information", DurationSeconds = 10 };
        using var controller = new StepUpLoggingController(opts);
        using var stepUpSink = new StepUpSink(stepUpInner, controller.LevelSwitch, controller.BaseLevel, []);

        var serilogRoot = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(stepUpSink)
            .WriteTo.Sink(new ImmediateSink(bypassLogger))
            .CreateLogger();

        using var factory = new SerilogLoggerFactory(serilogRoot, dispose: false);
        var logger = factory.CreateLogger("RoutingTest");

        using (logger.BeginImmediateScope())
        {
            logger.LogInformation("inside immediate scope");
            logger.LogInformation("also immediate");
        }

        logger.LogWarning("normal warning (not immediate)");

        // Both scoped logs go to bypass only
        Assert.Equal(2, bypassOutput.Events.Count);
        // Normal warning goes to step-up only (Warning >= Warning base level)
        Assert.Single(stepUpOutput.Events);
    }

    [Fact]
    public void MelScope_ImmediateScope_ScopeEndsCleanly()
    {
        var stepUpOutput = new CollectingSink();
        var bypassOutput = new CollectingSink();

        var stepUpInner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(stepUpOutput).CreateLogger();
        var bypassLogger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(bypassOutput).CreateLogger();

        var opts = new StepUpLoggingOptions { BaseLevel = "Warning", StepUpLevel = "Information", DurationSeconds = 10 };
        using var controller = new StepUpLoggingController(opts);
        using var stepUpSink = new StepUpSink(stepUpInner, controller.LevelSwitch, controller.BaseLevel, []);

        var serilogRoot = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(stepUpSink)
            .WriteTo.Sink(new ImmediateSink(bypassLogger))
            .CreateLogger();

        using var factory = new SerilogLoggerFactory(serilogRoot, dispose: false);
        var logger = factory.CreateLogger("RoutingTest");

        using (logger.BeginImmediateScope())
        {
            logger.LogInformation("inside");
        }

        // After scope ends, Information is back to normal routing (below Warning base level → nowhere)
        logger.LogInformation("outside");

        Assert.Single(bypassOutput.Events);   // only the one inside the scope
        Assert.Empty(stepUpOutput.Events);    // outside Information is below Warning gate
    }

    [Fact]
    public async Task MelHelper_LogImmediateError_TriggersStepUpViaTriggerSink()
    {
        var stepUpOutput = new CollectingSink();
        var bypassOutput = new CollectingSink();

        var stepUpInner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(stepUpOutput).CreateLogger();
        var bypassLogger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(bypassOutput).CreateLogger();

        var opts = new StepUpLoggingOptions { BaseLevel = "Warning", StepUpLevel = "Information", DurationSeconds = 10 };
        using var controller = new StepUpLoggingController(opts);
        using var stepUpSink = new StepUpSink(stepUpInner, controller.LevelSwitch, controller.BaseLevel, []);
        using var triggerSink = new StepUpTriggerSink(controller);
        using var immediateSink = new ImmediateSink(bypassLogger);

        var serilogRoot = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(stepUpSink)
            .WriteTo.Sink(triggerSink)
            .WriteTo.Sink(immediateSink)
            .CreateLogger();

        using var factory = new SerilogLoggerFactory(serilogRoot, dispose: false);
        var logger = factory.CreateLogger("RoutingTest");

        Assert.False(controller.IsSteppedUp);

        logger.LogImmediateError("immediate error");
        await Task.Delay(150); // let trigger channel process

        // Routed to bypass (IsImmediate), not step-up
        Assert.Single(bypassOutput.Events);
        Assert.Empty(stepUpOutput.Events);
        // Trigger still fired despite the IsImmediate marker
        Assert.True(controller.IsSteppedUp);
    }

    // ─── 7. LogProperties marker correctness ──────────────────────────────────

    [Fact]
    public void ImmediateEvent_HasIsImmediateProperty_InBypassOutput()
    {
        using var p = new Pipeline();

        p.Root.Write(MakeEvent(LogEventLevel.Information, Immediate()));

        var evt = Assert.Single(p.BypassOutput.Events);
        Assert.True(evt.Properties.TryGetValue(LogProperties.IsImmediate, out var val));
        Assert.IsType<ScalarValue>(val);
        Assert.Equal(true, ((ScalarValue)val!).Value);
    }

    [Fact]
    public void SummaryEvent_HasIsRequestSummaryProperty_InBypassOutput()
    {
        using var p = new Pipeline();

        p.Root.Write(MakeEvent(LogEventLevel.Information, Summary()));

        var evt = Assert.Single(p.BypassOutput.Events);
        Assert.True(evt.Properties.TryGetValue(LogProperties.IsRequestSummary, out var val));
        Assert.Equal(true, ((ScalarValue)val!).Value);
    }

    // ─── 8. NeverStepUpCategories deny-list wiring (ADR 0008) ──────────────────

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
            });
            builder.AddStepUpLogging();

            const string efToken = "BLANK_EF_TOKEN_b03";
            const string appToken = "BLANK_APP_TOKEN_b03";
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
            // The blank entries must not pin every category to BaseLevel: the app event still exports.
            Assert.Contains(appToken, contents);
            // The real entry still works alongside the (filtered) blanks.
            Assert.DoesNotContain(efToken, contents);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
