using BenchmarkDotNet.Attributes;
using Serilog;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Benchmarks;

/// <summary>
/// What the three sinks beside <see cref="StepUpSink"/> cost per event: <see cref="StepUpTriggerSink"/> for an
/// Error and for an event below it, <see cref="SummarySink"/> and <see cref="ImmediateSink"/> for an event
/// carrying their flag and one that does not. Each sink is called directly with prebuilt events, over a
/// target that only counts.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Logging")]
public class SideSinkBenchmarks
{
    private const int TriggerWaitMilliseconds = 2000;
    private const string TriggerMeter = "StepUpLogging.Sink";
    private const string ErrorEvents = "sink_error_events_total";

    private readonly NullLogEventSink _forwarded = new();
    private int _triggers;
    private Serilog.Core.Logger _target = null!;
    private StepUpTriggerSink _triggerSink = null!;
    private SummarySink _summarySink = null!;
    private ImmediateSink _immediateSink = null!;
    private LogEvent _error = null!;
    private LogEvent _belowError = null!;
    private LogEvent _summary = null!;
    private LogEvent _immediate = null!;
    private LogEvent _plain = null!;

    /// <summary>Builds the sinks and the events, and checks which event each sink reacts to.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _target = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_forwarded).CreateLogger();
        _triggerSink = new StepUpTriggerSink(() => Interlocked.Increment(ref _triggers));
        _summarySink = new SummarySink(_target);
        _immediateSink = new ImmediateSink(_target);
        _error = BenchmarkFixtures.HandledEvent(LogEventLevel.Error, extra: 4);
        _belowError = BenchmarkFixtures.HandledEvent(LogEventLevel.Warning, extra: 4);
        _summary = Tagged(LogProperties.IsRequestSummary);
        _immediate = Tagged(LogProperties.IsImmediate);
        _plain = BenchmarkFixtures.HandledEvent(LogEventLevel.Information, extra: 4);

        BenchmarkFixtures.Require(TriggersCounted(TriggerOnBelowError) == 0, "an event below Error requested a step-up");
        BenchmarkFixtures.Require(TriggersCounted(TriggerOnError) == 1, "an Error did not request a step-up once");
        BenchmarkFixtures.Require(SpinWait.SpinUntil(() => Volatile.Read(ref _triggers) >= 1, TriggerWaitMilliseconds), "the trigger sink never called the trigger");
        BenchmarkFixtures.Require(ForwardedBy(SummaryUntagged) == 0 && ForwardedBy(SummaryTagged) == 1, "the summary sink did not forward exactly the tagged event");
        BenchmarkFixtures.Require(ForwardedBy(ImmediateUntagged) == 0 && ForwardedBy(ImmediateTagged) == 1, "the immediate sink did not forward exactly the tagged event");
    }

    /// <summary>Disposes the sinks and the target under them.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _triggerSink.Dispose();
        _summarySink.Dispose();
        _immediateSink.Dispose();
        _target.Dispose();
    }

    /// <summary>An Error: counted and queued for the background task that calls the trigger.</summary>
    [Benchmark]
    public void TriggerOnError() => _triggerSink.Emit(_error);

    /// <summary>An event below Error: looked at and left alone.</summary>
    [Benchmark]
    public void TriggerOnBelowError() => _triggerSink.Emit(_belowError);

    /// <summary>A request summary: forwarded to the target.</summary>
    [Benchmark]
    public void SummaryTagged() => _summarySink.Emit(_summary);

    /// <summary>An ordinary event: looked at and left alone.</summary>
    [Benchmark]
    public void SummaryUntagged() => _summarySink.Emit(_plain);

    /// <summary>An event flagged immediate: forwarded to the target.</summary>
    [Benchmark]
    public void ImmediateTagged() => _immediateSink.Emit(_immediate);

    /// <summary>An ordinary event: looked at and left alone.</summary>
    [Benchmark]
    public void ImmediateUntagged() => _immediateSink.Emit(_plain);

    private static LogEvent Tagged(string flag)
    {
        var tagged = BenchmarkFixtures.HandledEvent(LogEventLevel.Information, extra: 4);
        tagged.AddPropertyIfAbsent(new LogEventProperty(flag, new ScalarValue(true)));
        return tagged;
    }

    private long TriggersCounted(Action emit) => BenchmarkFixtures.CountedBy(TriggerMeter, ErrorEvents, emit);

    private int ForwardedBy(Action emit)
    {
        var before = _forwarded.Received;
        emit();
        return _forwarded.Received - before;
    }
}
