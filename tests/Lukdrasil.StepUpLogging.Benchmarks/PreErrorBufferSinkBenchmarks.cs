using BenchmarkDotNet.Attributes;
using Serilog;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Benchmarks;

/// <summary>
/// What <see cref="PreErrorBufferSink"/> costs to hold an event under its trace, with one trace and with
/// more traces than it keeps (every hold then evicts), from one thread and from eight, and what a
/// flush on an Error costs. The sink is called directly with prebuilt events.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Logging")]
public class PreErrorBufferSinkBenchmarks
{
    private const int HoldsPerInvoke = 4096;
    private const int CapacityPerContext = 100;
    private const int MaxContexts = 1024;

    private readonly NullLogEventSink _flushed = new();
    private PreErrorBufferSink _sink = null!;
    private LogEvent[] _held = null!;
    private LogEvent[] _fillOneBuffer = null!;
    private LogEvent _error = null!;

    /// <summary>How many different traces the held events are spread over.</summary>
    [Params(1, 4096)]
    public int Contexts { get; set; }

    /// <summary>How many threads hold at the same time.</summary>
    [Params(1, 8)]
    public int Threads { get; set; }

    /// <summary>Builds the sink and the events, and checks how many traces the sink keeps.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var bypass = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_flushed).CreateLogger();
        _sink = new PreErrorBufferSink(bypass, CapacityPerContext, MaxContexts, LogEventLevel.Information);
        _held = [.. Enumerable.Range(0, HoldsPerInvoke).Select(i => InTrace(LogEventLevel.Information, i % Contexts))];
        _fillOneBuffer = [.. Enumerable.Range(0, CapacityPerContext).Select(_ => InTrace(LogEventLevel.Information, 0))];
        _error = InTrace(LogEventLevel.Error, 0);

        Hold();
        BenchmarkFixtures.Require(_sink.ContextCount == Math.Min(Contexts, MaxContexts), "the sink does not keep one buffer per trace up to its cap");
        FillAndFlushOnError();
        BenchmarkFixtures.Require(_flushed.Received == CapacityPerContext, "the Error did not flush a full buffer");
    }

    /// <summary>Drops the held events and disposes the sink.</summary>
    [GlobalCleanup]
    public void Cleanup() => _sink.Dispose();

    /// <summary>Holds <see cref="HoldsPerInvoke"/> events split across the threads; reported per hold, so its inverse is holds per second.</summary>
    [Benchmark(OperationsPerInvoke = HoldsPerInvoke)]
    public void Hold() =>
        Task.WaitAll([.. Enumerable.Range(0, Threads).Select(thread => Task.Run(() => HoldSlice(thread)))]);

    /// <summary>Holds a full buffer's worth of events in one trace, then emits an Error that flushes them to the bypass logger.</summary>
    [Benchmark]
    public void FillAndFlushOnError()
    {
        foreach (var held in _fillOneBuffer)
        {
            _sink.Hold(held);
        }

        _sink.Emit(_error);
    }

    private void HoldSlice(int thread)
    {
        for (var i = thread; i < _held.Length; i += Threads)
        {
            _sink.Hold(_held[i]);
        }
    }

    private static LogEvent InTrace(LogEventLevel level, int trace) =>
        BenchmarkFixtures.Event(level, BenchmarkFixtures.OrderHandlerContext, BenchmarkFixtures.Text("TraceId", $"{trace:x32}"));
}
