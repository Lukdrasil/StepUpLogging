using System.Diagnostics;
using Serilog.Events;
using static Lukdrasil.StepUpLogging.Tests.PrebufferHarness;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// <see cref="PreErrorBufferSink"/> splits its trace LRU into up to 16 stripes of at least 64 traces: a stripe evicts only
/// its own least recently touched trace, and a hold in one stripe never waits for a hold in another.
/// </summary>
public class PrebufferStripeTests
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OtherStripeDeadline = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DisposeBlockedWindow = TimeSpan.FromMilliseconds(200);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(16)]
    [InlineData(127)]
    public void StripeCountFor_SmallCaps_UseOneStripe(int maxContexts)
        => Assert.Equal(1, PreErrorBufferSink.StripeCountFor(maxContexts));

    [Theory]
    [InlineData(128, 2)]
    [InlineData(1024, 16)]
    [InlineData(100_000, 16)]
    public void StripeCountFor_LargeCaps_UseOneStripePer64TracesUpTo16(int maxContexts, int expectedStripes)
        => Assert.Equal(expectedStripes, PreErrorBufferSink.StripeCountFor(maxContexts));

    [Fact]
    public void StripeCapacities_SumToMaxContexts()
    {
        var capacities = PreErrorBufferSink.StripeCapacities(1030, 16);

        Assert.Equal(1030, capacities.Sum());
        Assert.Equal(Enumerable.Repeat(65, 6).Concat(Enumerable.Repeat(64, 10)), capacities);
    }

    [Fact]
    public void Hold_MoreTracesThanMaxContexts_KeepsExactlyMaxContexts()
    {
        Activity.Current = null;
        using var sink = new PreErrorBufferSink(BypassInto(new Collector()), capacityPerContext: 1, maxContexts: 2048, minimumLevel: LogEventLevel.Information);

        for (var i = 0; i < 4096; i++)
        {
            sink.Hold(Held($"trace-{i}", "held"));
        }

        Assert.Equal(2048, sink.ContextCount);
    }

    [Fact]
    public void Hold_InOneStripe_EvictsItsLeastRecentlyTouchedTrace()
    {
        Activity.Current = null;
        var collector = new Collector();
        using var sink = new PreErrorBufferSink(BypassInto(collector), capacityPerContext: 5, maxContexts: 128, minimumLevel: LogEventLevel.Information);
        var traces = TracesInStripe(sink, stripe: 0, count: 65);

        foreach (var trace in traces.Take(64))
        {
            sink.Hold(Held(trace, $"{trace}-1"));
        }
        sink.Hold(Held(traces[0], $"{traces[0]}-2"));
        sink.Hold(Held(traces[64], $"{traces[64]}-1"));

        sink.Emit(Error(traces[1]));
        Assert.Empty(collector.Events);

        sink.Emit(Error(traces[0]));
        Assert.Equal([$"{traces[0]}-1", $"{traces[0]}-2"], Texts(collector.Events));
    }

    [Fact]
    public void Hold_FullStripe_DoesNotEvictAnotherStripe()
    {
        Activity.Current = null;
        var collector = new Collector();
        using var sink = new PreErrorBufferSink(BypassInto(collector), capacityPerContext: 5, maxContexts: 128, minimumLevel: LogEventLevel.Information);
        var otherStripeTrace = TracesInStripe(sink, stripe: 1, count: 1)[0];

        sink.Hold(Held(otherStripeTrace, "other-stripe"));
        foreach (var trace in TracesInStripe(sink, stripe: 0, count: 200))
        {
            sink.Hold(Held(trace, trace));
        }
        sink.Emit(Error(otherStripeTrace));

        Assert.Equal(["other-stripe"], Texts(collector.Events));
    }

    [Fact]
    public void Hold_InAnotherStripe_DoesNotWaitForAHoldInProgress()
    {
        Activity.Current = null;
        using var sink = new PreErrorBufferSink(BypassInto(new Collector()), capacityPerContext: 5, maxContexts: 128, minimumLevel: LogEventLevel.Information);
        var parkedTrace = TracesInStripe(sink, stripe: 0, count: 1)[0];
        var freeTrace = TracesInStripe(sink, stripe: 1, count: 1)[0];
        var parked = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var freeHoldDone = new ManualResetEventSlim();
        sink.BeforeEnqueueTestHook = ParkFirstHold(parked, release);

        var parkedHold = StartBackground(() => sink.Hold(Held(parkedTrace, "parked")));
        Thread? freeHold = null;
        try
        {
            Assert.True(parked.Wait(Bounded, TestContext.Current.CancellationToken), "the stripe-0 hold never reached the enqueue hook");
            freeHold = StartBackground(() =>
            {
                sink.Hold(Held(freeTrace, "free"));
                freeHoldDone.Set();
            });

            Assert.True(freeHoldDone.Wait(OtherStripeDeadline, TestContext.Current.CancellationToken), "a hold in stripe 1 waited for the hold parked in stripe 0");
        }
        finally
        {
            release.Set();
            parkedHold.Join(Bounded);
            freeHold?.Join(Bounded);
            sink.BeforeEnqueueTestHook = null;
        }
    }

    [Fact]
    public void Dispose_WhileAHoldIsInsideItsStripe_LeavesNoContext()
    {
        Activity.Current = null;
        var collector = new Collector();
        var sink = new PreErrorBufferSink(BypassInto(collector), capacityPerContext: 5, maxContexts: 128, minimumLevel: LogEventLevel.Information);
        var parked = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var disposed = new ManualResetEventSlim();
        sink.BeforeEnqueueTestHook = ParkFirstHold(parked, release);

        var parkedHold = StartBackground(() => sink.Hold(Held("parked-trace", "parked")));
        try
        {
            Assert.True(parked.Wait(Bounded, TestContext.Current.CancellationToken), "the hold never reached the enqueue hook");
            StartBackground(() =>
            {
                sink.Dispose();
                disposed.Set();
            });

            Assert.False(disposed.Wait(DisposeBlockedWindow, TestContext.Current.CancellationToken), "Dispose returned while a hold was still inside its stripe");
        }
        finally
        {
            release.Set();
            parkedHold.Join(Bounded);
        }

        Assert.True(disposed.Wait(Bounded, TestContext.Current.CancellationToken), "Dispose never returned after the parked hold finished");
        Assert.Equal(0, sink.ContextCount);

        sink.Hold(Held("parked-trace", "after-dispose"));
        sink.Hold(Held("fresh-trace", "after-dispose"));
        sink.Emit(Error("parked-trace"));
        sink.Emit(Error("fresh-trace"));

        Assert.Equal(0, sink.ContextCount);
        Assert.Empty(collector.Events);
    }

    /// <summary>
    /// An enqueue hook that parks the first hold to reach it until <paramref name="release"/> is set (at most
    /// <see cref="Bounded"/>), signalling <paramref name="parked"/>; every later hold passes straight through.
    /// </summary>
    private static Action ParkFirstHold(ManualResetEventSlim parked, ManualResetEventSlim release)
    {
        var parkedOnce = 0;
        return () =>
        {
            if (Interlocked.Exchange(ref parkedOnce, 1) == 0)
            {
                parked.Set();
                release.Wait(Bounded);
            }
        };
    }
}
