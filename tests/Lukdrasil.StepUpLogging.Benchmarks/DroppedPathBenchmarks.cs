using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Serilog;
using Serilog.Core;
using Serilog.Enrichers.OpenTelemetry;
using Serilog.Events;
using Serilog.Exceptions;

namespace Lukdrasil.StepUpLogging.Benchmarks;

/// <summary>
/// Where the cost of a dropped Debug event goes. Each row is a Serilog logger one stage longer than the row
/// above it, built in the order the Verbose root of <c>AddStepUpLogging()</c> runs them: the root enrichers
/// first, then the library sinks. A row minus the row above it is what that stage costs a Debug event,
/// with the root enrichers applied to every event (the root's gate, ADR 0026, is not part of these loggers).
/// Plain is a Verbose logger over a sink that drops everything.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Logging")]
[Config(typeof(NuGetDependencyConfig))]
public class DroppedPathBenchmarks
{
    private readonly NullLogEventSink _exported = new();
    private readonly NullLogEventSink _terminal = new();
    private StepUpLoggingController _controller = null!;
    private PreErrorBufferSink _buffer = null!;
    private StepUpSink _stepUpSink = null!;
    private StepUpTriggerSink _triggerSink = null!;
    private SummarySink _summarySink = null!;
    private ImmediateSink _immediateSink = null!;
    private Serilog.ILogger[] _rungs = null!;
    private Activity? _activity;

    /// <summary>Whether an <see cref="Activity"/> is current, so the trace and span id enrichers have something to read.</summary>
    [Params(false, true)]
    public bool InActivity { get; set; }

    /// <summary>Builds the sinks and the loggers, and checks the longest logger drops a Debug event and exports a Warning once.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _activity = InActivity ? BenchmarkFixtures.StartTrace() : null;
        _controller = new StepUpLoggingController(new StepUpLoggingOptions());
        var output = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_exported).CreateLogger();
        _buffer = new PreErrorBufferSink(Logger.None, 100, 1024, _controller.StepUpLevel);
        _stepUpSink = new StepUpSink(output, _controller.LevelSwitch, _controller.BaseLevel, [], _buffer, categoryFloors: null, () => _controller.IsDiagnosticActive);
        _triggerSink = new StepUpTriggerSink(_controller);
        _summarySink = new SummarySink(Logger.None);
        _immediateSink = new ImmediateSink(Logger.None);

        var stages = Stages().ToArray();
        _rungs = [.. Enumerable.Range(0, stages.Length + 1).Select(count => Rung(stages.Take(count)))];

        var longest = _rungs[^1];
        BenchmarkFixtures.Require(Exported(() => longest.Debug(BenchmarkFixtures.HandledTemplate, 42, "an ordinary value")) == 0, "a Debug event was exported");
        BenchmarkFixtures.Require(Exported(() => longest.Warning(BenchmarkFixtures.HandledTemplate, 42, "an ordinary value")) == 1, "a Warning event was not exported once");
    }

    /// <summary>Disposes the sinks and the controller, and stops the activity.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _triggerSink.Dispose();
        _stepUpSink.Dispose();
        _buffer.Dispose();
        _summarySink.Dispose();
        _immediateSink.Dispose();
        _controller.Dispose();
        BenchmarkFixtures.StopTrace(_activity);
    }

    /// <summary>A Verbose logger over a sink that drops everything: the baseline.</summary>
    [Benchmark(Baseline = true)]
    public void Plain() => Debug(0);

    /// <summary>Plus <c>FromLogContext</c>.</summary>
    [Benchmark]
    public void LogContext() => Debug(1);

    /// <summary>Plus the trace id and span id enrichers.</summary>
    [Benchmark]
    public void TraceIds() => Debug(2);

    /// <summary>Plus <see cref="ActivityContextEnricher"/>.</summary>
    [Benchmark]
    public void ActivityContext() => Debug(3);

    /// <summary>Plus the Application, Environment and MachineName enrichers.</summary>
    [Benchmark]
    public void Properties() => Debug(4);

    /// <summary>Plus the exception-details enricher.</summary>
    [Benchmark]
    public void ExceptionDetails() => Debug(5);

    /// <summary>Plus <see cref="StepUpSink"/> with the pre-error buffer wired in.</summary>
    [Benchmark]
    public void StepUpSink() => Debug(6);

    /// <summary>Plus <see cref="PreErrorBufferSink"/> itself.</summary>
    [Benchmark]
    public void PreErrorBuffer() => Debug(7);

    /// <summary>Plus <see cref="StepUpTriggerSink"/>.</summary>
    [Benchmark]
    public void Trigger() => Debug(8);

    /// <summary>Plus <see cref="SummarySink"/>.</summary>
    [Benchmark]
    public void Summary() => Debug(9);

    /// <summary>Plus <see cref="ImmediateSink"/>: the whole root pipeline.</summary>
    [Benchmark]
    public void Immediate() => Debug(10);

    private void Debug(int rung) => _rungs[rung].Debug(BenchmarkFixtures.HandledTemplate, 42, "an ordinary value");

    private int Exported(Action log)
    {
        var before = _exported.Received;
        log();
        return _exported.Received - before;
    }

    private IEnumerable<Action<LoggerConfiguration>> Stages()
    {
        yield return lc => lc.Enrich.FromLogContext();
        yield return lc =>
        {
            lc.Enrich.WithOpenTelemetryTraceId();
            lc.Enrich.WithOpenTelemetrySpanId();
        };
        yield return lc => lc.Enrich.With<ActivityContextEnricher>();
        yield return lc =>
        {
            lc.Enrich.WithProperty("Application", "Benchmarks");
            lc.Enrich.WithProperty("Environment", "Production");
            lc.Enrich.WithMachineName();
        };
        yield return lc => lc.Enrich.WithExceptionDetails();
        yield return lc => lc.WriteTo.Sink(_stepUpSink);
        yield return lc => lc.WriteTo.Sink(_buffer);
        yield return lc => lc.WriteTo.Sink(_triggerSink);
        yield return lc => lc.WriteTo.Sink(_summarySink);
        yield return lc => lc.WriteTo.Sink(_immediateSink);
    }

    private Serilog.ILogger Rung(IEnumerable<Action<LoggerConfiguration>> stages)
    {
        var configuration = new LoggerConfiguration().MinimumLevel.Verbose();
        foreach (var stage in stages)
        {
            stage(configuration);
        }

        return configuration.WriteTo.Sink(_terminal).CreateLogger()
            .ForContext("SourceContext", BenchmarkFixtures.OrderHandlerContext);
    }
}

/// <summary>
/// Lets the ladder reference <c>Serilog.Enrichers.OpenTelemetry</c> directly: that NuGet package ships a
/// non-optimized build, which BenchmarkDotNet's validator rejects for any assembly the benchmark names. The
/// library itself calls the same package, so the ladder measures what the library runs.
/// </summary>
internal sealed class NuGetDependencyConfig : ManualConfig
{
    public NuGetDependencyConfig() => WithOptions(ConfigOptions.DisableOptimizationsValidator);
}
