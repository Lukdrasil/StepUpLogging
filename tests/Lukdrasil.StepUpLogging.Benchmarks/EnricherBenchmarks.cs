using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Benchmarks;

/// <summary>
/// What each root enricher adds to an event. Every row builds a fresh event, so
/// <see cref="NewEvent"/> is the baseline to subtract: it is the cost of the event itself.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Logging")]
public class EnricherBenchmarks
{
    private ActivityContextEnricher _activityContext = null!;
    private AlwaysExportEnricher _alwaysExport = null!;
    private RedactionEnricher _redaction = null!;
    private Activity? _activity;

    /// <summary>Whether an <see cref="Activity"/> is current while the enrichers run.</summary>
    [Params(false, true)]
    public bool InActivity { get; set; }

    /// <summary>How many string properties each event carries on top of its two template arguments.</summary>
    [Params(4, 16)]
    public int StringProperties { get; set; }

    /// <summary>Builds the enrichers and checks each one does its work on the event.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _activity = InActivity ? BenchmarkFixtures.StartTrace() : null;
        _activityContext = new ActivityContextEnricher();
        _alwaysExport = new AlwaysExportEnricher(["MyApp.Orders"]);
        _redaction = new RedactionEnricher(BenchmarkFixtures.SamplePatterns());

        BenchmarkFixtures.Require(ActivityContext().Properties.ContainsKey("TraceFlags") == InActivity, "ActivityContextEnricher does not follow the current activity");
        BenchmarkFixtures.Require(AlwaysExport().Properties.ContainsKey(LogProperties.IsImmediate), "AlwaysExportEnricher did not mark the event");
        BenchmarkFixtures.Require(RedactsASecret(), "RedactionEnricher did not mask a matching property");
    }

    /// <summary>Stops the activity the setup started.</summary>
    [GlobalCleanup]
    public void Cleanup() => BenchmarkFixtures.StopTrace(_activity);

    /// <summary>The event alone, with no enricher: the baseline.</summary>
    [Benchmark(Baseline = true)]
    public LogEvent NewEvent() => BenchmarkFixtures.HandledEvent(LogEventLevel.Information, StringProperties);

    /// <summary>The event with <see cref="ActivityContextEnricher"/> applied.</summary>
    [Benchmark]
    public LogEvent ActivityContext() => Enriched(_activityContext);

    /// <summary>The event with <see cref="AlwaysExportEnricher"/> applied, its category listed.</summary>
    [Benchmark]
    public LogEvent AlwaysExport() => Enriched(_alwaysExport);

    /// <summary>The event with <see cref="RedactionEnricher"/> applied over five patterns, none matching.</summary>
    [Benchmark]
    public LogEvent RedactProperties() => Enriched(_redaction);

    private LogEvent Enriched(Serilog.Core.ILogEventEnricher enricher)
    {
        var logEvent = NewEvent();
        enricher.Enrich(logEvent, BenchmarkFixtures.PropertyFactory);
        return logEvent;
    }

    private bool RedactsASecret()
    {
        var logEvent = BenchmarkFixtures.Event(LogEventLevel.Information, BenchmarkFixtures.OrderHandlerContext, BenchmarkFixtures.Text("Url", "/api/token/xyz9"));
        _redaction.Enrich(logEvent, BenchmarkFixtures.PropertyFactory);
        return logEvent.Properties["Url"] is ScalarValue { Value: "/api/[REDACTED]" };
    }
}
