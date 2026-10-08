using System.Diagnostics;
using Serilog.Events;
using static Lukdrasil.StepUpLogging.Tests.PrebufferHarness;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Parallel holds on separate traces of <see cref="PreErrorBufferSink"/> lose, duplicate and reorder nothing, and a
/// Dispose racing them leaves no context and exports nothing.
/// </summary>
public class PrebufferConcurrencyTests
{
    private const int Threads = 8;
    private const int EventsPerTrace = 10;
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(10);

    [Fact]
    public void Hold_EightThreadsOnSeparateTraces_FlushesEveryEventOnceInOrder()
    {
        Activity.Current = null;
        var collector = new Collector();
        using var sink = new PreErrorBufferSink(BypassInto(collector), capacityPerContext: EventsPerTrace, maxContexts: 1024, minimumLevel: LogEventLevel.Information);
        var traces = Enumerable.Range(0, Threads).Select(t => $"thread-{t}").ToArray();
        var expected = traces.Select(ExpectedTexts).ToArray();
        var held = expected.Select((texts, t) => texts.Select(text => Held(traces[t], text)).ToArray()).ToArray();
        var errors = traces.Select(Error).ToArray();
        var start = new ManualResetEventSlim();

        var workers = Enumerable.Range(0, Threads).Select(t => StartBackground(() =>
        {
            start.Wait(Bounded);
            foreach (var logEvent in held[t])
            {
                sink.Hold(logEvent);
            }
            sink.Emit(errors[t]);
        })).ToArray();
        start.Set();

        Assert.All(workers, worker => Assert.True(worker.Join(Bounded), "a holding thread did not finish"));
        foreach (var error in errors)
        {
            sink.Emit(error);
        }

        for (var t = 0; t < Threads; t++)
        {
            Assert.Equal(expected[t], Texts(collector.Events.Where(e => TraceOf(e) == traces[t])));
        }
        Assert.Equal(Threads * EventsPerTrace, collector.Events.Count);
    }

    [Fact]
    public void Dispose_DuringParallelHolds_ExportsNothingAndLeavesNoContext()
    {
        Activity.Current = null;
        var collector = new Collector();
        var sink = new PreErrorBufferSink(BypassInto(collector), capacityPerContext: EventsPerTrace, maxContexts: 1024, minimumLevel: LogEventLevel.Information);
        var traces = Enumerable.Range(0, Threads).Select(t => $"thread-{t}").ToArray();
        var held = traces.Select(trace => Held(trace, $"{trace}-held")).ToArray();
        var everyThreadHeld = new CountdownEvent(Threads);
        var stop = new ManualResetEventSlim();

        var workers = Enumerable.Range(0, Threads).Select(t => StartBackground(() =>
        {
            sink.Hold(held[t]);
            everyThreadHeld.Signal();
            while (!stop.IsSet)
            {
                sink.Hold(held[t]);
            }
        })).ToArray();
        try
        {
            Assert.True(everyThreadHeld.Wait(Bounded, TestContext.Current.CancellationToken), "not every thread held an event before Dispose");
            sink.Dispose();
        }
        finally
        {
            stop.Set();
        }

        Assert.All(workers, worker => Assert.True(worker.Join(Bounded), "a holding thread did not stop"));
        foreach (var trace in traces)
        {
            sink.Emit(Error(trace));
        }

        Assert.Equal(0, sink.ContextCount);
        Assert.Empty(collector.Events);
    }

    private static string[] ExpectedTexts(string trace)
        => Enumerable.Range(1, EventsPerTrace).Select(i => $"{trace}-e{i}").ToArray();
}
