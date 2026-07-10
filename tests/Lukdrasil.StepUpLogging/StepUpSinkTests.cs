using System;
using System.Collections.Generic;
using System.Linq;
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
}
