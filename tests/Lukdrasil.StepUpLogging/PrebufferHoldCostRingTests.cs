using System.Diagnostics;
using Serilog.Events;
using static Lukdrasil.StepUpLogging.Tests.PrebufferHarness;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// A trace's ring in <see cref="PreErrorBufferSink"/> grows from 4 slots to 16 and then straight to its capacity, so a
/// full trace allocates about one capacity of slots, and every growth step keeps the newest events in order.
/// </summary>
public class PrebufferHoldCostRingTests
{
    private const int WarmUpTraces = 256;
    private const int MeasuredTraces = 1024;

    [Theory]
    [InlineData(3, 400)]
    [InlineData(10, 420)]
    [InlineData(16, 420)]
    [InlineData(17, 1400)]
    [InlineData(100, 1400)]
    public void Hold_ATraceOfNEvents_AllocatesUnderTheBound(int held, long maxBytes)
    {
        Activity.Current = null;
        using var sink = new PreErrorBufferSink(BypassInto(new Collector()), capacityPerContext: 100, maxContexts: 64, minimumLevel: LogEventLevel.Information);
        var events = Enumerable.Range(0, WarmUpTraces + MeasuredTraces).Select(i => Held($"trace-{i}", "held")).ToArray();

        HoldEach(sink, events[..WarmUpTraces], held);
        var before = GC.GetAllocatedBytesForCurrentThread();
        HoldEach(sink, events[WarmUpTraces..], held);
        var bytesPerTrace = (GC.GetAllocatedBytesForCurrentThread() - before) / MeasuredTraces;

        Assert.True(bytesPerTrace < maxBytes, $"a trace of {held} events allocated {bytesPerTrace} B, expected under {maxBytes} B");
    }

    [Theory]
    [InlineData(100, 4)]
    [InlineData(100, 5)]
    [InlineData(100, 16)]
    [InlineData(100, 17)]
    [InlineData(100, 100)]
    [InlineData(100, 101)]
    [InlineData(10, 11)]
    [InlineData(20, 25)]
    public void Hold_AcrossEachGrowthStep_FlushesTheNewestInOrder(int capacity, int held)
    {
        var oldestKept = Math.Max(1, held - capacity + 1);

        Assert.Equal(Enumerable.Range(oldestKept, held - oldestKept + 1).Select(i => $"e{i}"), HoldThenFlush(capacity, held));
    }

    private static void HoldEach(PreErrorBufferSink sink, LogEvent[] traces, int held)
    {
        foreach (var trace in traces)
        {
            for (var i = 0; i < held; i++)
            {
                sink.Hold(trace);
            }
        }
    }
}
