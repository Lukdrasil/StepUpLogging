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

    [Fact]
    public void Buffer_NonW3CActivity_WithTraceIdProperty_UsesPropertyKey()
    {
        Activity.Current = null;
        var savedFormat = Activity.DefaultIdFormat;
        var savedForce = Activity.ForceDefaultIdFormat;
        Activity.DefaultIdFormat = ActivityIdFormat.Hierarchical;
        Activity.ForceDefaultIdFormat = true;

        var collector = new CollectingSink();
        var bypass = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        using var sink = new PreErrorBufferSink(bypass, capacityPerContext: 5, maxContexts: 16);
        var parser = new MessageTemplateParser();

        // Two separate TraceId values so we can confirm key isolation
        var traceAProps = new[] { new LogEventProperty("TraceId", new ScalarValue("trace-a")) };
        var traceBProps = new[] { new LogEventProperty("TraceId", new ScalarValue("trace-b")) };

        var activity = new Activity("Hierarchical").Start();
        try
        {
            Assert.Equal(ActivityIdFormat.Hierarchical, activity.IdFormat);

            sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Debug, null, parser.Parse("a-1"), traceAProps));
            sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Debug, null, parser.Parse("b-1"), traceBProps));

            // Error only on trace-a; only a-1 should flush
            sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error, null, parser.Parse("err-a"), traceAProps));
        }
        finally
        {
            activity.Dispose();
            Activity.DefaultIdFormat = savedFormat;
            Activity.ForceDefaultIdFormat = savedForce;
        }

        Assert.Single(collector.Events);
        Assert.Equal("a-1", collector.Events[0].MessageTemplate.Text);
    }

    [Fact]
    public void Buffer_ImmediateEvents_NotBuffered_NoDoubleEmitOnFlush()
    {
        var collector = new CollectingSink();
        var bypass = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(collector)
            .CreateLogger();

        using var sink = new PreErrorBufferSink(bypass, capacityPerContext: 10, maxContexts: 16);
        var parser = new MessageTemplateParser();

        var immediateProps = new[] { new LogEventProperty(LogProperties.IsImmediate, new ScalarValue(true)) };
        var normalProps = Array.Empty<LogEventProperty>();

        // One normal event and one immediate event before the error
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, parser.Parse("normal"), normalProps));
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, parser.Parse("immediate"), immediateProps));

        // Trigger flush via error
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error, null, parser.Parse("err"), normalProps));

        // Only the normal event should be flushed; the immediate one must not appear
        Assert.Single(collector.Events);
        Assert.Equal("normal", collector.Events[0].MessageTemplate.Text);
    }

    [Fact]
    public void Buffer_SummaryEvents_NotBuffered_NoDoubleEmitOnFlush()
    {
        var collector = new CollectingSink();
        var bypass = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(collector)
            .CreateLogger();

        using var sink = new PreErrorBufferSink(bypass, capacityPerContext: 10, maxContexts: 16);
        var parser = new MessageTemplateParser();

        var summaryProps = new[] { new LogEventProperty(LogProperties.IsRequestSummary, new ScalarValue(true)) };
        var normalProps = Array.Empty<LogEventProperty>();

        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, parser.Parse("normal"), normalProps));
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, parser.Parse("summary"), summaryProps));

        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error, null, parser.Parse("err"), normalProps));

        Assert.Single(collector.Events);
        Assert.Equal("normal", collector.Events[0].MessageTemplate.Text);
    }

    [Fact]
    public void Error_ForUnseenContext_CreatesNoBufferAndConsumesNoLruSlot()
    {
        Activity.Current = null;
        var collector = new CollectingSink();
        var bypass = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        using var sink = new PreErrorBufferSink(bypass, capacityPerContext: 5, maxContexts: 16);
        var parser = new MessageTemplateParser();

        var newCtx = new[] { new LogEventProperty("TraceId", new ScalarValue("brand-new")) };

        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error, null, parser.Parse("err"), newCtx));

        Assert.Equal(0, sink.ContextCount);
        Assert.Empty(collector.Events);
    }

    [Fact]
    public void Buffer_LRU_EvictsLeastRecentlyTouched_KeepsRecent()
    {
        Activity.Current = null;
        var collector = new CollectingSink();
        var bypass = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        using var sink = new PreErrorBufferSink(bypass, capacityPerContext: 5, maxContexts: 2);
        var parser = new MessageTemplateParser();

        static LogEventProperty[] Ctx(string id) => new[] { new LogEventProperty("TraceId", new ScalarValue(id)) };

        // a and b buffered
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, parser.Parse("a1"), Ctx("a")));
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, parser.Parse("b1"), Ctx("b")));

        // Touch a again so b becomes least-recently-touched
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, parser.Parse("a2"), Ctx("a")));

        // Adding c exceeds maxContexts (2) → evict b (oldest touch)
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, parser.Parse("c1"), Ctx("c")));

        Assert.Equal(2, sink.ContextCount);

        // Error on b: evicted, nothing flushes
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error, null, parser.Parse("err-b"), Ctx("b")));
        Assert.Empty(collector.Events);

        // Error on a: survived, both a1 and a2 flush
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error, null, parser.Parse("err-a"), Ctx("a")));
        Assert.Collection(collector.Events,
            e => Assert.Equal("a1", e.MessageTemplate.Text),
            e => Assert.Equal("a2", e.MessageTemplate.Text));
    }

    [Fact]
    public void Buffer_ThenErrorSameContext_StillFlushes()
    {
        Activity.Current = null;
        var collector = new CollectingSink();
        var bypass = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        using var sink = new PreErrorBufferSink(bypass, capacityPerContext: 10, maxContexts: 16);
        var parser = new MessageTemplateParser();

        var ctx = new[] { new LogEventProperty("TraceId", new ScalarValue("same")) };

        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Debug, null, parser.Parse("d1"), ctx));
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, parser.Parse("i1"), ctx));
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error, null, parser.Parse("err"), ctx));

        Assert.Collection(collector.Events,
            e => Assert.Equal("d1", e.MessageTemplate.Text),
            e => Assert.Equal("i1", e.MessageTemplate.Text));
    }

    [Fact]
    public void Buffer_WhitespaceTraceIdProperty_FallsBackToGlobalKey()
    {
        Activity.Current = null;
        var collector = new CollectingSink();
        var bypass = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();
        using var sink = new PreErrorBufferSink(bypass, capacityPerContext: 5, maxContexts: 16);
        var parser = new MessageTemplateParser();

        // Whitespace TraceId → falls back to "__global__"
        var whitespaceProp = new[] { new LogEventProperty("TraceId", new ScalarValue("   ")) };
        var noProp = Array.Empty<LogEventProperty>();

        // Both events share the global key — error on one flushes the other
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Debug, null, parser.Parse("global-1"), whitespaceProp));
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error, null, parser.Parse("err"), noProp));

        Assert.Single(collector.Events);
        Assert.Equal("global-1", collector.Events[0].MessageTemplate.Text);
    }
}
