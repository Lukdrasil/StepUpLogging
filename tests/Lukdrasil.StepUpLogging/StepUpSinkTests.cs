using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Lukdrasil.StepUpLogging.Tests;

public class StepUpSinkTests
{
    private static LogEvent MakeEvent(LogEventLevel level, params LogEventProperty[] properties)
    {
        var parser = new MessageTemplateParser();
        return new LogEvent(DateTimeOffset.UtcNow, level, null, parser.Parse("test"), properties);
    }

    private static LogEventProperty SourceContext(string value) =>
        new("SourceContext", new ScalarValue(value));

    private const string EfCommandCategory = "Microsoft.EntityFrameworkCore.Database.Command";

    [Fact]
    public void SteppedUp_ListedCategory_InformationEvent_IsDropped()
    {
        var collector = new CollectingSink();
        var inner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
        using var sink = new StepUpSink(inner, levelSwitch, LogEventLevel.Warning, [EfCommandCategory]);

        sink.Emit(MakeEvent(LogEventLevel.Information, SourceContext(EfCommandCategory)));

        Assert.Empty(collector.Events);
    }

    [Fact]
    public void SteppedUp_ListedCategory_WarningEvent_Passes()
    {
        var collector = new CollectingSink();
        var inner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
        using var sink = new StepUpSink(inner, levelSwitch, LogEventLevel.Warning, [EfCommandCategory]);

        sink.Emit(MakeEvent(LogEventLevel.Warning, SourceContext(EfCommandCategory)));

        Assert.Single(collector.Events);
    }

    [Fact]
    public void SteppedUp_UnlistedCategory_InformationEvent_Passes()
    {
        var collector = new CollectingSink();
        var inner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
        using var sink = new StepUpSink(inner, levelSwitch, LogEventLevel.Warning, [EfCommandCategory]);

        sink.Emit(MakeEvent(LogEventLevel.Information, SourceContext("My.App.Service")));

        Assert.Single(collector.Events);
    }

    [Fact]
    public void SteppedUp_PrefixBoundary_DotChild_Dropped_OtherSuffix_Passes()
    {
        var collector = new CollectingSink();
        var inner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
        using var sink = new StepUpSink(inner, levelSwitch, LogEventLevel.Warning, [EfCommandCategory]);

        sink.Emit(MakeEvent(LogEventLevel.Information, SourceContext(EfCommandCategory + ".Internal")));
        Assert.Empty(collector.Events);

        sink.Emit(MakeEvent(LogEventLevel.Information, SourceContext("Microsoft.EntityFrameworkCore.Database.CommandBuilder")));
        Assert.Single(collector.Events);
    }

    [Fact]
    public void SteppedUp_MatchIsOrdinalCaseSensitive_LowercaseDoesNotMatch()
    {
        var collector = new CollectingSink();
        var inner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
        using var sink = new StepUpSink(inner, levelSwitch, LogEventLevel.Warning, [EfCommandCategory]);

        sink.Emit(MakeEvent(LogEventLevel.Information, SourceContext("microsoft.entityframeworkcore.database.command")));

        Assert.Single(collector.Events);
    }

    [Fact]
    public void SteppedUp_NoSourceContext_NeverMatched()
    {
        var collector = new CollectingSink();
        var inner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
        using var sink = new StepUpSink(inner, levelSwitch, LogEventLevel.Warning, [EfCommandCategory]);

        sink.Emit(MakeEvent(LogEventLevel.Information));

        Assert.Single(collector.Events);
    }

    [Fact]
    public void SteppedUp_EmptyDenyList_MatchesPreChangeBehaviour()
    {
        var collector = new CollectingSink();
        var inner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
        using var sink = new StepUpSink(inner, levelSwitch, LogEventLevel.Warning, []);

        sink.Emit(MakeEvent(LogEventLevel.Information, SourceContext(EfCommandCategory)));

        Assert.Single(collector.Events);
    }

    [Fact]
    public void NotSteppedUp_ListedCategory_WarningEvent_Passes()
    {
        var collector = new CollectingSink();
        var inner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Warning);
        using var sink = new StepUpSink(inner, levelSwitch, LogEventLevel.Warning, [EfCommandCategory]);

        sink.Emit(MakeEvent(LogEventLevel.Warning, SourceContext(EfCommandCategory)));

        Assert.Single(collector.Events);
    }

    [Fact]
    public void SteppedUp_BypassMarkers_StillDroppedAfterGate()
    {
        var collector = new CollectingSink();
        var inner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
        using var sink = new StepUpSink(inner, levelSwitch, LogEventLevel.Warning, [EfCommandCategory]);

        var summary = new LogEventProperty(LogProperties.IsRequestSummary, new ScalarValue(true));
        var immediate = new LogEventProperty(LogProperties.IsImmediate, new ScalarValue(true));
        sink.Emit(MakeEvent(LogEventLevel.Warning, summary, SourceContext("My.App")));
        sink.Emit(MakeEvent(LogEventLevel.Warning, immediate, SourceContext("My.App")));

        Assert.Empty(collector.Events);
    }

    [Fact]
    public void StepUpSink_Forwards_EventsAtOrAboveLevelSwitch()
    {
        var collector = new CollectingSink();
        var inner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Warning);
        using var sink = new StepUpSink(inner, levelSwitch, LogEventLevel.Warning, []);

        sink.Emit(MakeEvent(LogEventLevel.Warning));
        sink.Emit(MakeEvent(LogEventLevel.Error));

        Assert.Equal(2, collector.Events.Count);
    }

    [Fact]
    public void StepUpSink_Drops_EventsBelowLevelSwitch()
    {
        var collector = new CollectingSink();
        var inner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Warning);
        using var sink = new StepUpSink(inner, levelSwitch, LogEventLevel.Warning, []);

        sink.Emit(MakeEvent(LogEventLevel.Debug));
        sink.Emit(MakeEvent(LogEventLevel.Information));

        Assert.Empty(collector.Events);
    }

    [Fact]
    public void StepUpSink_Drops_IsRequestSummaryEvents()
    {
        var collector = new CollectingSink();
        var inner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Verbose);
        using var sink = new StepUpSink(inner, levelSwitch, LogEventLevel.Warning, []);

        var summaryProp = new LogEventProperty(LogProperties.IsRequestSummary, new ScalarValue(true));
        sink.Emit(MakeEvent(LogEventLevel.Information, summaryProp));

        Assert.Empty(collector.Events);
    }

    [Fact]
    public void StepUpSink_Drops_IsImmediateEvents()
    {
        var collector = new CollectingSink();
        var inner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Verbose);
        using var sink = new StepUpSink(inner, levelSwitch, LogEventLevel.Warning, []);

        var immediateProp = new LogEventProperty(LogProperties.IsImmediate, new ScalarValue(true));
        sink.Emit(MakeEvent(LogEventLevel.Information, immediateProp));

        Assert.Empty(collector.Events);
    }

    [Fact]
    public void StepUpSink_Forwards_IsImmediateFalse_AsNormalEvent()
    {
        var collector = new CollectingSink();
        var inner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Verbose);
        using var sink = new StepUpSink(inner, levelSwitch, LogEventLevel.Warning, []);

        // IsImmediate=false should not be treated as a bypass marker
        var immediateProp = new LogEventProperty(LogProperties.IsImmediate, new ScalarValue(false));
        sink.Emit(MakeEvent(LogEventLevel.Information, immediateProp));

        Assert.Single(collector.Events);
    }

    [Fact]
    public void StepUpSink_RespondsToLevelSwitchChange()
    {
        var collector = new CollectingSink();
        var inner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Warning);
        using var sink = new StepUpSink(inner, levelSwitch, LogEventLevel.Warning, []);

        sink.Emit(MakeEvent(LogEventLevel.Information)); // dropped
        Assert.Empty(collector.Events);

        levelSwitch.MinimumLevel = LogEventLevel.Information;
        sink.Emit(MakeEvent(LogEventLevel.Information)); // now passes
        Assert.Single(collector.Events);
    }

    [Fact]
    public void StepUpSink_AfterDispose_DoesNotForward()
    {
        var collector = new CollectingSink();
        var inner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Verbose);
        var sink = new StepUpSink(inner, levelSwitch, LogEventLevel.Warning, []);

        sink.Dispose();
        sink.Emit(MakeEvent(LogEventLevel.Warning));

        Assert.Empty(collector.Events);
    }

    [Fact]
    public void StepUpSink_NullInnerLogger_ThrowsArgumentNullException()
    {
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
        Assert.Throws<ArgumentNullException>(() => new StepUpSink(null!, levelSwitch, LogEventLevel.Warning, []));
    }

    [Fact]
    public void StepUpSink_NullLevelSwitch_ThrowsArgumentNullException()
    {
        var inner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink()).CreateLogger();
        Assert.Throws<ArgumentNullException>(() => new StepUpSink(inner, null!, LogEventLevel.Warning, []));
    }

    [Fact]
    public void StepUpSink_IsImmediate_NonBoolScalar_PassesThrough()
    {
        var collector = new CollectingSink();
        var inner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Verbose);
        using var sink = new StepUpSink(inner, levelSwitch, LogEventLevel.Warning, []);

        // "yes" is a string scalar, not a bool — IsBoolTrue returns false → event passes through
        var prop = new LogEventProperty(LogProperties.IsImmediate, new ScalarValue("yes"));
        sink.Emit(MakeEvent(LogEventLevel.Information, prop));

        Assert.Single(collector.Events);
    }

    [Fact]
    public void StepUpSink_IsImmediate_NonScalarValue_PassesThrough()
    {
        var collector = new CollectingSink();
        var inner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Verbose);
        using var sink = new StepUpSink(inner, levelSwitch, LogEventLevel.Warning, []);

        // SequenceValue is not a ScalarValue — IsBoolTrue returns false → event passes through
        var prop = new LogEventProperty(LogProperties.IsImmediate, new SequenceValue(Enumerable.Empty<LogEventPropertyValue>()));
        sink.Emit(MakeEvent(LogEventLevel.Information, prop));

        Assert.Single(collector.Events);
    }

    [Fact]
    public void ImmediateEvent_GoesToImmediateSink_NotStepUpSink()
    {
        var stepUpCollector = new CollectingSink();
        var immediateCollector = new CollectingSink();

        var stepUpInner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(stepUpCollector).CreateLogger();
        var bypassLogger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(immediateCollector).CreateLogger();

        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
        using var stepUpSink = new StepUpSink(stepUpInner, levelSwitch, LogEventLevel.Warning, []);
        using var immediateSink = new ImmediateSink(bypassLogger);

        var immediateProp = new LogEventProperty(LogProperties.IsImmediate, new ScalarValue(true));
        var evt = MakeEvent(LogEventLevel.Information, immediateProp);

        stepUpSink.Emit(evt);
        immediateSink.Emit(evt);

        Assert.Empty(stepUpCollector.Events);
        Assert.Single(immediateCollector.Events);
    }

    [Fact]
    public void SteppedUp_ListedCategory_SurvivesRedaction_StillDropped()
    {
        // ADR 0021 regression: RedactionEnricher must exclude SourceContext even when a configured
        // pattern would otherwise match it, or the NeverStepUpCategories deny-list this sink checks
        // silently stops matching once redaction is turned on.
        var collector = new CollectingSink();
        var inner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
        using var sink = new StepUpSink(inner, levelSwitch, LogEventLevel.Warning, [EfCommandCategory]);

        var enricher = new RedactionEnricher(new CompiledRedactionPatterns(new[] { new Regex(".*", RegexOptions.Compiled) }));
        var logEvent = MakeEvent(LogEventLevel.Information, SourceContext(EfCommandCategory));
        enricher.Enrich(logEvent, new SimpleLogEventPropertyFactory());

        Assert.Equal(EfCommandCategory, ((ScalarValue)logEvent.Properties["SourceContext"]).Value);

        sink.Emit(logEvent);

        Assert.Empty(collector.Events);
    }

    private static (StepUpSink Sink, PreErrorBufferSink Buffer, CollectingSink Exported, CollectingSink Flushed) WithHeldBackBuffer(
        LogEventLevel switchLevel, string[] neverStepUpCategories)
    {
        var exported = new CollectingSink();
        var flushed = new CollectingSink();
        var inner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(exported).CreateLogger();
        var bypass = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(flushed).CreateLogger();
        var buffer = new PreErrorBufferSink(bypass, capacityPerContext: 10, maxContexts: 16, minimumLevel: LogEventLevel.Information);
        var sink = new StepUpSink(inner, new LoggingLevelSwitch(switchLevel), LogEventLevel.Warning, neverStepUpCategories, buffer);
        return (sink, buffer, exported, flushed);
    }

    private static LogEvent Tagged(LogEventLevel level, string text, params LogEventProperty[] properties) =>
        new(DateTimeOffset.UtcNow, level, null, new MessageTemplateParser().Parse(text), properties);

    [Fact]
    public void RejectedEvent_AtOrAboveStepUpLevel_ReachesTheBuffer()
    {
        var (sink, buffer, exported, flushed) = WithHeldBackBuffer(LogEventLevel.Warning, []);

        sink.Emit(Tagged(LogEventLevel.Information, "rejected-info"));
        buffer.Emit(Tagged(LogEventLevel.Error, "err"));

        Assert.Empty(exported.Events);
        Assert.Equal("rejected-info", Assert.Single(flushed.Events).MessageTemplate.Text);
    }

    [Fact]
    public void ExportedEvent_DoesNotReachTheBuffer()
    {
        var (sink, buffer, exported, flushed) = WithHeldBackBuffer(LogEventLevel.Warning, []);

        sink.Emit(Tagged(LogEventLevel.Warning, "exported-warn"));
        sink.Emit(Tagged(LogEventLevel.Information, "rejected-info"));
        buffer.Emit(Tagged(LogEventLevel.Error, "err"));

        Assert.Equal("exported-warn", Assert.Single(exported.Events).MessageTemplate.Text);
        Assert.Equal("rejected-info", Assert.Single(flushed.Events).MessageTemplate.Text);
    }

    [Fact]
    public void SteppedUp_ExportedInformation_DoesNotReachTheBuffer()
    {
        var (sink, buffer, exported, flushed) = WithHeldBackBuffer(LogEventLevel.Information, []);

        sink.Emit(Tagged(LogEventLevel.Information, "exported-info"));
        buffer.Emit(Tagged(LogEventLevel.Error, "err"));

        Assert.Single(exported.Events);
        Assert.Empty(flushed.Events);
    }

    [Fact]
    public void BypassMarkedEvent_DoesNotReachTheBuffer()
    {
        var (sink, buffer, exported, flushed) = WithHeldBackBuffer(LogEventLevel.Warning, []);

        sink.Emit(Tagged(LogEventLevel.Information, "immediate", new LogEventProperty(LogProperties.IsImmediate, new ScalarValue(true))));
        sink.Emit(Tagged(LogEventLevel.Information, "summary", new LogEventProperty(LogProperties.IsRequestSummary, new ScalarValue(true))));
        sink.Emit(Tagged(LogEventLevel.Information, "rejected-info"));
        buffer.Emit(Tagged(LogEventLevel.Error, "err"));

        Assert.Empty(exported.Events);
        Assert.Equal("rejected-info", Assert.Single(flushed.Events).MessageTemplate.Text);
    }

    [Fact]
    public void SteppedUp_ListedCategory_RejectedInformation_ReachesTheBuffer()
    {
        var (sink, buffer, exported, flushed) = WithHeldBackBuffer(LogEventLevel.Information, [EfCommandCategory]);

        sink.Emit(Tagged(LogEventLevel.Information, "ef-info", SourceContext(EfCommandCategory)));
        buffer.Emit(Tagged(LogEventLevel.Error, "err"));

        Assert.Empty(exported.Events);
        Assert.Equal("ef-info", Assert.Single(flushed.Events).MessageTemplate.Text);
    }

    private const string FlooredCategory = "Test.Floored";
    private const string ExemptCategory = "Test.Exempt";

    private static (StepUpSink Sink, PreErrorBufferSink Buffer, CollectingSink Exported, CollectingSink Flushed) WithFloors(
        LogEventLevel switchLevel,
        (string Key, LogEventLevel Floor)[] floors,
        string[]? diagnosticExempt = null,
        string[]? neverStepUpCategories = null,
        LogEventLevel baseLevel = LogEventLevel.Warning,
        Func<bool>? isDiagnosticActive = null)
    {
        var exported = new CollectingSink();
        var flushed = new CollectingSink();
        var inner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(exported).CreateLogger();
        var bypass = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(flushed).CreateLogger();
        var buffer = new PreErrorBufferSink(bypass, capacityPerContext: 10, maxContexts: 16, minimumLevel: LogEventLevel.Verbose);
        var map = new CategoryFloorMap(floors.ToDictionary(f => f.Key, f => f.Floor, StringComparer.Ordinal), diagnosticExempt ?? []);
        var sink = new StepUpSink(inner, new LoggingLevelSwitch(switchLevel), baseLevel, neverStepUpCategories ?? [], buffer, map, isDiagnosticActive);
        return (sink, buffer, exported, flushed);
    }

    private static string[] Texts(CollectingSink sink) => sink.Events.Select(e => e.MessageTemplate.Text).ToArray();

    [Fact]
    public void SteppedUp_FlooredCategory_InformationBelowFloor_IsHeldBack()
    {
        var (sink, buffer, exported, flushed) = WithFloors(LogEventLevel.Information, [(FlooredCategory, LogEventLevel.Warning)]);

        sink.Emit(Tagged(LogEventLevel.Information, "floored-info", SourceContext(FlooredCategory)));
        buffer.Emit(Tagged(LogEventLevel.Error, "err"));

        Assert.Empty(exported.Events);
        Assert.Equal(["floored-info"], Texts(flushed));
    }

    [Fact]
    public void SteppedUp_FlooredCategory_EventAtTheFloor_IsExported()
    {
        var (sink, _, exported, _) = WithFloors(LogEventLevel.Information, [(FlooredCategory, LogEventLevel.Warning)]);

        sink.Emit(Tagged(LogEventLevel.Warning, "floored-warn", SourceContext(FlooredCategory + ".Child")));

        Assert.Equal(["floored-warn"], Texts(exported));
    }

    [Fact]
    public void SteppedUp_FloorBelowTheSwitch_ChangesNothing()
    {
        var (sink, _, exported, _) = WithFloors(LogEventLevel.Information, [(FlooredCategory, LogEventLevel.Debug)]);

        sink.Emit(Tagged(LogEventLevel.Information, "floored-info", SourceContext(FlooredCategory)));
        sink.Emit(Tagged(LogEventLevel.Debug, "floored-debug", SourceContext(FlooredCategory)));

        Assert.Equal(["floored-info"], Texts(exported));
    }

    [Fact]
    public void SteppedUp_UnflooredCategory_IsNotAffectedByAnotherCategorysFloor()
    {
        var (sink, _, exported, _) = WithFloors(LogEventLevel.Information, [(FlooredCategory, LogEventLevel.Warning)]);

        sink.Emit(Tagged(LogEventLevel.Information, "other-info", SourceContext("Test.Other")));
        sink.Emit(Tagged(LogEventLevel.Information, "no-context-info"));

        Assert.Equal(["other-info", "no-context-info"], Texts(exported));
    }

    [Fact]
    public void SteppedUp_MostSpecificFloorKeyDecides()
    {
        var (sink, _, exported, _) = WithFloors(
            LogEventLevel.Information,
            [("Test", LogEventLevel.Warning), (FlooredCategory, LogEventLevel.Information)]);

        sink.Emit(Tagged(LogEventLevel.Information, "child-info", SourceContext(FlooredCategory + ".Child")));
        sink.Emit(Tagged(LogEventLevel.Information, "other-info", SourceContext("Test.Other")));

        Assert.Equal(["child-info"], Texts(exported));
    }

    [Fact]
    public void NeverStepUpPinAboveTheFloor_PinWins()
    {
        var (sink, _, exported, _) = WithFloors(
            LogEventLevel.Information,
            [(FlooredCategory, LogEventLevel.Information)],
            neverStepUpCategories: [FlooredCategory]);

        sink.Emit(Tagged(LogEventLevel.Information, "pinned-info", SourceContext(FlooredCategory)));

        Assert.Empty(exported.Events);
    }

    [Fact]
    public void FloorAboveTheNeverStepUpPin_FloorWins()
    {
        var (sink, _, exported, _) = WithFloors(
            LogEventLevel.Debug,
            [(FlooredCategory, LogEventLevel.Warning)],
            neverStepUpCategories: [FlooredCategory],
            baseLevel: LogEventLevel.Information);

        sink.Emit(Tagged(LogEventLevel.Information, "floored-info", SourceContext(FlooredCategory)));
        sink.Emit(Tagged(LogEventLevel.Warning, "floored-warn", SourceContext(FlooredCategory)));

        Assert.Equal(["floored-warn"], Texts(exported));
    }

    [Fact]
    public void Diagnostic_NonExemptFlooredInformation_IsExported_ExemptOneIsHeldBack()
    {
        var (sink, buffer, exported, flushed) = WithFloors(
            LogEventLevel.Information,
            [(FlooredCategory, LogEventLevel.Warning), (ExemptCategory, LogEventLevel.Warning)],
            diagnosticExempt: [ExemptCategory],
            isDiagnosticActive: () => true);

        sink.Emit(Tagged(LogEventLevel.Information, "floored-info", SourceContext(FlooredCategory)));
        sink.Emit(Tagged(LogEventLevel.Information, "exempt-info", SourceContext(ExemptCategory)));
        buffer.Emit(Tagged(LogEventLevel.Error, "err"));

        Assert.Equal(["floored-info"], Texts(exported));
        Assert.Equal(["exempt-info"], Texts(flushed));
    }

    [Fact]
    public void Diagnostic_NeverStepUpIsNotApplied()
    {
        var (sink, _, exported, _) = WithFloors(
            LogEventLevel.Information,
            [],
            neverStepUpCategories: [EfCommandCategory],
            isDiagnosticActive: () => true);

        sink.Emit(Tagged(LogEventLevel.Information, "ef-info", SourceContext(EfCommandCategory)));

        Assert.Equal(["ef-info"], Texts(exported));
    }

    [Fact]
    public void Diagnostic_FlagIsReadPerEvent()
    {
        var diagnostic = false;
        var (sink, _, exported, _) = WithFloors(
            LogEventLevel.Information,
            [(FlooredCategory, LogEventLevel.Warning)],
            neverStepUpCategories: [EfCommandCategory],
            isDiagnosticActive: () => diagnostic);

        sink.Emit(Tagged(LogEventLevel.Information, "floored-before", SourceContext(FlooredCategory)));
        sink.Emit(Tagged(LogEventLevel.Information, "ef-before", SourceContext(EfCommandCategory)));
        diagnostic = true;
        sink.Emit(Tagged(LogEventLevel.Information, "floored-during", SourceContext(FlooredCategory)));
        sink.Emit(Tagged(LogEventLevel.Information, "ef-during", SourceContext(EfCommandCategory)));

        Assert.Equal(["floored-during", "ef-during"], Texts(exported));
    }

    [Fact]
    public void Diagnostic_EventBelowTheSwitch_IsStillHeldBack()
    {
        var (sink, buffer, exported, flushed) = WithFloors(
            LogEventLevel.Information,
            [(FlooredCategory, LogEventLevel.Warning)],
            isDiagnosticActive: () => true);

        sink.Emit(Tagged(LogEventLevel.Debug, "floored-debug", SourceContext(FlooredCategory)));
        buffer.Emit(Tagged(LogEventLevel.Error, "err"));

        Assert.Empty(exported.Events);
        Assert.Equal(["floored-debug"], Texts(flushed));
    }

    private static long AllocatedBytesOfSecondRun(Action run)
    {
        run();
        var before = GC.GetAllocatedBytesForCurrentThread();
        run();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void StepUpSink_DroppedBelowLevelSwitch_DoesNotAllocate()
    {
        var exported = new CollectingSink();
        var inner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(exported).CreateLogger();
        using var sink = new StepUpSink(inner, new LoggingLevelSwitch(LogEventLevel.Warning), LogEventLevel.Warning, []);
        var debug = Tagged(LogEventLevel.Debug, "dropped-debug");

        var delta = AllocatedBytesOfSecondRun(() =>
        {
            for (var i = 0; i < 10_000; i++) sink.Emit(debug);
        });

        Assert.Equal(0, delta);
        Assert.Empty(exported.Events);
    }

    [Fact]
    public void StepUpSink_DroppedWithDenyListAndFloors_DoesNotAllocate()
    {
        var exported = new CollectingSink();
        var inner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(exported).CreateLogger();
        var floors = new CategoryFloorMap(new Dictionary<string, LogEventLevel>(StringComparer.Ordinal) { [FlooredCategory] = LogEventLevel.Warning }, []);
        using var sink = new StepUpSink(inner, new LoggingLevelSwitch(LogEventLevel.Information), LogEventLevel.Warning, [EfCommandCategory], categoryFloors: floors);
        LogEvent[] dropped =
        [
            Tagged(LogEventLevel.Information, "floored-info", SourceContext(FlooredCategory)),
            Tagged(LogEventLevel.Information, "ef-info", SourceContext(EfCommandCategory)),
            Tagged(LogEventLevel.Debug, "app-debug", SourceContext("My.App.Service")),
        ];

        var delta = AllocatedBytesOfSecondRun(() =>
        {
            for (var i = 0; i < 10_000; i++) sink.Emit(dropped[i % dropped.Length]);
        });

        Assert.Equal(0, delta);
        Assert.Empty(exported.Events);
    }
}
