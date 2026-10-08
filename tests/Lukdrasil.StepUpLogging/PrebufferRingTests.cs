using System.Diagnostics;
using Serilog.Events;
using static Lukdrasil.StepUpLogging.Tests.PrebufferHarness;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// A trace's buffer in <see cref="PreErrorBufferSink"/> starts small and grows on demand up to its capacity, then
/// overwrites its oldest event; a flush always yields the held events oldest first.
/// </summary>
public class PrebufferRingTests
{
    private const string Trace = "ring-trace";

    [Fact]
    public void Hold_FewerEventsThanCapacity_FlushesAllInOrder()
        => Assert.Equal(Numbered(1, 7), HoldThenFlush(capacity: 10, held: 7));

    [Fact]
    public void Hold_MoreEventsThanCapacity_FlushesTheNewestCapacityInOrder()
        => Assert.Equal(Numbered(4, 13), HoldThenFlush(capacity: 10, held: 13));

    [Fact]
    public void Hold_MoreEventsThanACapacityBelowTheInitialSlots_FlushesTheNewestInOrder()
        => Assert.Equal(Numbered(3, 5), HoldThenFlush(capacity: 3, held: 5));

    [Fact]
    public void Error_AfterAFlushAndTwoMoreHolds_FlushesExactlyThoseTwo()
    {
        Activity.Current = null;
        var collector = new Collector();
        using var sink = new PreErrorBufferSink(BypassInto(collector), capacityPerContext: 5, maxContexts: 16, minimumLevel: LogEventLevel.Information);

        HoldNumbered(sink, 1, 7);
        sink.Emit(Error(Trace));
        HoldNumbered(sink, 8, 9);
        sink.Emit(Error(Trace));

        Assert.Equal(Numbered(3, 7).Concat(Numbered(8, 9)), Texts(collector.Events));
    }

    [Fact]
    public void Hold_FirstEventOfNewTrace_AllocatesFarLessThanCapacitySlots()
    {
        const int warmUpTraces = 256;
        const int measuredTraces = 1024;
        const long maxBytesPerNewTrace = 400;
        Activity.Current = null;
        using var sink = new PreErrorBufferSink(BypassInto(new Collector()), capacityPerContext: 100, maxContexts: 64, minimumLevel: LogEventLevel.Information);
        var events = Enumerable.Range(0, warmUpTraces + measuredTraces).Select(i => Held($"trace-{i}", "held")).ToArray();

        for (var i = 0; i < warmUpTraces; i++)
        {
            sink.Hold(events[i]);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = warmUpTraces; i < events.Length; i++)
        {
            sink.Hold(events[i]);
        }
        var bytesPerNewTrace = (GC.GetAllocatedBytesForCurrentThread() - before) / measuredTraces;

        Assert.True(bytesPerNewTrace < maxBytesPerNewTrace, $"the first hold of a new trace allocated {bytesPerNewTrace} B, expected under {maxBytesPerNewTrace} B");
        Assert.Equal(64, sink.ContextCount);
    }

    private static string[] HoldThenFlush(int capacity, int held)
    {
        Activity.Current = null;
        var collector = new Collector();
        using var sink = new PreErrorBufferSink(BypassInto(collector), capacity, maxContexts: 16, minimumLevel: LogEventLevel.Information);

        HoldNumbered(sink, 1, held);
        sink.Emit(Error(Trace));

        return Texts(collector.Events);
    }

    private static void HoldNumbered(PreErrorBufferSink sink, int first, int last)
    {
        for (var i = first; i <= last; i++)
        {
            sink.Hold(Held(Trace, $"e{i}"));
        }
    }

    private static IEnumerable<string> Numbered(int first, int last)
        => Enumerable.Range(first, last - first + 1).Select(i => $"e{i}");
}
