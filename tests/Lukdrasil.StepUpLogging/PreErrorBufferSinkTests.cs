using System;
using System.Collections.Generic;
using System.Diagnostics;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Lukdrasil.StepUpLogging.Tests;

internal sealed class CollectingSink : ILogEventSink
{
    public List<LogEvent> Events { get; } = new();
    public void Emit(LogEvent logEvent)
    {
        Events.Add(logEvent);
    }
}

public class PreErrorBufferSinkTests
{
    [Fact]
    public void Buffer_BeforeError_FlushesOnError()
    {
        var collector = new CollectingSink();
        var bypass = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(collector)
            .CreateLogger();

        using var sink = new PreErrorBufferSink(bypass, capacityPerContext: 10, maxContexts: 16);
        var parser = new MessageTemplateParser();

        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Debug, null, parser.Parse("dbg1"), Array.Empty<LogEventProperty>()));
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, parser.Parse("info1"), Array.Empty<LogEventProperty>()));

        // Trigger flush
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error, null, parser.Parse("err"), Array.Empty<LogEventProperty>()));

        Assert.Collection(collector.Events,
            e => Assert.Equal("dbg1", e.MessageTemplate.Text),
            e => Assert.Equal("info1", e.MessageTemplate.Text));
        Assert.Equal(2, collector.Events.Count);
        Assert.DoesNotContain(collector.Events, e => e.MessageTemplate.Text == "err");
    }

    [Fact]
    public void Buffer_Capacity_DropsOldest()
    {
        var collector = new CollectingSink();
        var bypass = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(collector)
            .CreateLogger();

        using var sink = new PreErrorBufferSink(bypass, capacityPerContext: 2, maxContexts: 16);
        var parser = new MessageTemplateParser();

        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Debug, null, parser.Parse("a"), Array.Empty<LogEventProperty>()));
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Debug, null, parser.Parse("b"), Array.Empty<LogEventProperty>()));
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Debug, null, parser.Parse("c"), Array.Empty<LogEventProperty>()));
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error, null, parser.Parse("err"), Array.Empty<LogEventProperty>()));

        // Only last two before error should be flushed
        Assert.Collection(collector.Events,
            e => Assert.Equal("b", e.MessageTemplate.Text),
            e => Assert.Equal("c", e.MessageTemplate.Text));
    }

    [Fact]
    public void NoFlush_OnNonError()
    {
        var collector = new CollectingSink();
        var bypass = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(collector)
            .CreateLogger();

        using var sink = new PreErrorBufferSink(bypass, capacityPerContext: 5, maxContexts: 16);
        var parser = new MessageTemplateParser();

        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Debug, null, parser.Parse("x"), Array.Empty<LogEventProperty>()));
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Warning, null, parser.Parse("w"), Array.Empty<LogEventProperty>()));

        Assert.Empty(collector.Events);
    }

    [Fact]
    public void Buffer_PerContextIsolation_FlushesOnlySameTrace()
    {
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;

        var collector = new CollectingSink();
        var bypass = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(collector)
            .CreateLogger();

        using var sink = new PreErrorBufferSink(bypass, capacityPerContext: 10, maxContexts: 16);
        var parser = new MessageTemplateParser();

        // First, create separate root trace A2
        using (var a2 = new Activity("A2").Start())
        {
            sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Debug, null, parser.Parse("a2-1"), Array.Empty<LogEventProperty>()));
        }

        // Now create a different root trace A1 and flush there
        using (var a1 = new Activity("A1").Start())
        {
            sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Debug, null, parser.Parse("a1-1"), Array.Empty<LogEventProperty>()));
            sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Debug, null, parser.Parse("a1-2"), Array.Empty<LogEventProperty>()));
            sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error, null, parser.Parse("err-a1"), Array.Empty<LogEventProperty>()));
        }

        // Should flush only A1's pre-error events
        Assert.Collection(collector.Events,
            e => Assert.Equal("a1-1", e.MessageTemplate.Text),
            e => Assert.Equal("a1-2", e.MessageTemplate.Text));
        Assert.Equal(2, collector.Events.Count);
        Assert.DoesNotContain(collector.Events, e => e.MessageTemplate.Text == "a2-1");
    }

    [Fact]
    public void Buffer_LRU_EvictsOldest_PreventsOldFlush()
    {
        var collector = new CollectingSink();
        var bypass = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(collector)
            .CreateLogger();

        using var sink = new PreErrorBufferSink(bypass, capacityPerContext: 5, maxContexts: 1);
        var parser = new MessageTemplateParser();

        // Simulate two contexts using explicit TraceId properties
        var ctx1Prop = new LogEventProperty("TraceId", new ScalarValue("ctx1"));
        var ctx2Prop = new LogEventProperty("TraceId", new ScalarValue("ctx2"));

        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, parser.Parse("c1-1"), new[] { ctx1Prop }));
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, parser.Parse("c1-2"), new[] { ctx1Prop }));

        // Touch ctx2 so LRU exceeds and evicts ctx1
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, parser.Parse("c2-1"), new[] { ctx2Prop }));

        // Now trigger error on ctx1; since it was evicted, nothing should flush for ctx1
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error, null, parser.Parse("err-c1"), new[] { ctx1Prop }));

        Assert.Empty(collector.Events);
    }

    [Fact]
    public void Buffer_LRU_KeepsMostRecent_AllowsFlush()
    {
        var collector = new CollectingSink();
        var bypass = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(collector)
            .CreateLogger();

        using var sink = new PreErrorBufferSink(bypass, capacityPerContext: 5, maxContexts: 1);
        var parser = new MessageTemplateParser();

        var ctx1Prop = new LogEventProperty("TraceId", new ScalarValue("ctx1"));
        var ctx2Prop = new LogEventProperty("TraceId", new ScalarValue("ctx2"));

        // Fill ctx1, then touch ctx2 (ctx1 evicted)
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, parser.Parse("c1-1"), new[] { ctx1Prop }));
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, parser.Parse("c2-1"), new[] { ctx2Prop }));

        // Flush ctx2 should yield its pre-event
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error, null, parser.Parse("err-c2"), new[] { ctx2Prop }));

        Assert.Collection(collector.Events,
            e => Assert.Equal("c2-1", e.MessageTemplate.Text));
    }
}
