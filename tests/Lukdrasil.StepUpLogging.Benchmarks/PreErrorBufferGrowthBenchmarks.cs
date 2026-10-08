using BenchmarkDotNet.Attributes;
using Serilog;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Benchmarks;

/// <summary>
/// What <see cref="PreErrorBufferSink"/> costs to hold a whole trace's worth of events when more traces pass through
/// than it keeps: each hold of a new trace allocates that trace's buffer, which grows from its first slots to the
/// events it holds, and evicts another trace. It is reported per trace, so the bytes show what one trace costs at
/// each size. The sink is called directly with prebuilt events from one thread.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Logging")]
public class PreErrorBufferGrowthBenchmarks
{
    private const int Traces = 4096;
    private const int CapacityPerContext = 100;
    private const int MaxContexts = 1024;

    private readonly NullLogEventSink _flushed = new();
    private PreErrorBufferSink _sink = null!;
    private LogEvent[] _held = null!;
    private LogEvent _error = null!;

    /// <summary>How many events each trace holds before the next trace begins.</summary>
    [Params(3, 10, 100)]
    public int HeldPerTrace { get; set; }

    /// <summary>Builds the sink and one event per trace, and checks how many traces it keeps and what an Error flushes.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var bypass = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_flushed).CreateLogger();
        _sink = new PreErrorBufferSink(bypass, CapacityPerContext, MaxContexts, LogEventLevel.Information);
        _held = [.. Enumerable.Range(0, Traces).Select(trace => InTrace(LogEventLevel.Information, trace))];
        _error = InTrace(LogEventLevel.Error, Traces - 1);

        HoldTraces();
        BenchmarkFixtures.Require(_sink.ContextCount == MaxContexts, "the sink does not keep exactly its cap of traces");
        _sink.Emit(_error);
        BenchmarkFixtures.Require(_flushed.Received == HeldPerTrace, "the Error did not flush exactly the events its trace held");
    }

    /// <summary>Drops the held events and disposes the sink.</summary>
    [GlobalCleanup]
    public void Cleanup() => _sink.Dispose();

    /// <summary>Holds <see cref="HeldPerTrace"/> events in each of <see cref="Traces"/> traces; reported per trace.</summary>
    [Benchmark(OperationsPerInvoke = Traces)]
    public void HoldTraces()
    {
        foreach (var held in _held)
        {
            for (var i = 0; i < HeldPerTrace; i++)
            {
                _sink.Hold(held);
            }
        }
    }

    private static LogEvent InTrace(LogEventLevel level, int trace) =>
        BenchmarkFixtures.Event(level, BenchmarkFixtures.OrderHandlerContext, BenchmarkFixtures.Text("TraceId", $"{trace:x32}"));
}
