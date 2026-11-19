using System.Diagnostics;
using Serilog.Events;
using static Lukdrasil.StepUpLogging.Tests.PrebufferHarness;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// A Dispose racing parallel holds on separate traces of <see cref="PreErrorBufferSink"/> leaves no context and
/// exports nothing.
/// </summary>
public class PrebufferConcurrencyTests
{
    private const int Threads = 8;
    private const int EventsPerTrace = 10;
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(10);

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
}
