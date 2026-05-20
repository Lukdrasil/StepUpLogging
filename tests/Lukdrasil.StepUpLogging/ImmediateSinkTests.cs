using System;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Lukdrasil.StepUpLogging.Tests;

public class ImmediateSinkTests
{
    private static LogEvent MakeEvent(LogEventLevel level, params LogEventProperty[] properties)
    {
        var parser = new MessageTemplateParser();
        return new LogEvent(DateTimeOffset.UtcNow, level, null, parser.Parse("test"), properties);
    }

    [Fact]
    public void ImmediateSink_Forwards_IsImmediateTrueEvents()
    {
        var collector = new CollectingSink();
        var target = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        using var sink = new ImmediateSink(target);

        var prop = new LogEventProperty(LogProperties.IsImmediate, new ScalarValue(true));
        sink.Emit(MakeEvent(LogEventLevel.Information, prop));

        Assert.Single(collector.Events);
    }

    [Fact]
    public void ImmediateSink_Drops_IsImmediateFalseEvents()
    {
        var collector = new CollectingSink();
        var target = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        using var sink = new ImmediateSink(target);

        var prop = new LogEventProperty(LogProperties.IsImmediate, new ScalarValue(false));
        sink.Emit(MakeEvent(LogEventLevel.Information, prop));

        Assert.Empty(collector.Events);
    }

    [Fact]
    public void ImmediateSink_Drops_EventsWithoutIsImmediate()
    {
        var collector = new CollectingSink();
        var target = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        using var sink = new ImmediateSink(target);

        sink.Emit(MakeEvent(LogEventLevel.Warning));

        Assert.Empty(collector.Events);
    }

    [Fact]
    public void ImmediateSink_Drops_IsImmediateNonBoolValue()
    {
        var collector = new CollectingSink();
        var target = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        using var sink = new ImmediateSink(target);

        var prop = new LogEventProperty(LogProperties.IsImmediate, new ScalarValue("yes"));
        sink.Emit(MakeEvent(LogEventLevel.Information, prop));

        Assert.Empty(collector.Events);
    }

    [Fact]
    public void ImmediateSink_Forwards_AtAllLogLevels()
    {
        var collector = new CollectingSink();
        var target = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        using var sink = new ImmediateSink(target);

        var prop = new LogEventProperty(LogProperties.IsImmediate, new ScalarValue(true));
        sink.Emit(MakeEvent(LogEventLevel.Debug, prop));
        sink.Emit(MakeEvent(LogEventLevel.Information, prop));
        sink.Emit(MakeEvent(LogEventLevel.Warning, prop));
        sink.Emit(MakeEvent(LogEventLevel.Error, prop));

        Assert.Equal(4, collector.Events.Count);
    }

    [Fact]
    public void ImmediateSink_AfterDispose_DoesNotForward()
    {
        var collector = new CollectingSink();
        var target = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        var sink = new ImmediateSink(target);

        sink.Dispose();

        var prop = new LogEventProperty(LogProperties.IsImmediate, new ScalarValue(true));
        sink.Emit(MakeEvent(LogEventLevel.Information, prop));

        Assert.Empty(collector.Events);
    }

    [Fact]
    public void ImmediateSink_DoesNotThrow_OnNullEvent()
    {
        var collector = new CollectingSink();
        var target = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        using var sink = new ImmediateSink(target);

        var ex = Record.Exception(() => sink.Emit(null!));
        Assert.Null(ex);
    }

    [Fact]
    public void ImmediateSink_NullTarget_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ImmediateSink(null!));
    }
}
