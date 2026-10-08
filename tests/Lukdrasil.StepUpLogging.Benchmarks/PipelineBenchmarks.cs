using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Lukdrasil.StepUpLogging.Benchmarks;

/// <summary>
/// What one application log call costs through the whole pipeline <c>AddStepUpLogging()</c> wires, at the
/// three outcomes an event can have at the default levels: dropped below the pre-error buffer's level,
/// held back in the pre-error buffer, exported. The baseline is the same call on a plain Serilog logger
/// over a sink that drops everything, so each row minus the baseline is what the library adds.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Logging")]
public class PipelineBenchmarks
{
    private const string BufferMeter = "StepUpLogging.Buffer";
    private const string BufferedEvents = "buffer_events_total";

    private static readonly Dictionary<PipelineScenario, KeyValuePair<string, string?>[]> ScenarioSettings = new()
    {
        [PipelineScenario.Default] = [],
        [PipelineScenario.Redaction] = [.. BenchmarkFixtures.RedactionSettings()],
        [PipelineScenario.FloorsAndNeverStepUp] =
        [
            BenchmarkFixtures.StepUpSetting("CategoryFloors:Microsoft.AspNetCore", "Warning"),
            BenchmarkFixtures.StepUpSetting("CategoryFloors:System.Net.Http", "Warning"),
            BenchmarkFixtures.StepUpSetting("CategoryFloors:MyApp", "Warning"),
            BenchmarkFixtures.StepUpSetting("NeverStepUpCategories:0", "Microsoft.AspNetCore.Hosting.Diagnostics"),
            BenchmarkFixtures.StepUpSetting("NeverStepUpCategories:1", "Microsoft.Extensions.Http"),
        ],
    };

    private static readonly Dictionary<PipelineScenario, Action<IServiceProvider, LoggerConfiguration>> ScenarioHooks = new()
    {
        [PipelineScenario.ConsumerRootSink] = (_, lc) => lc.WriteTo.Sink(new NullLogEventSink()),
    };

    private IHost _host = null!;
    private Serilog.ILogger _logger = null!;
    private Microsoft.Extensions.Logging.ILogger _melLogger = null!;
    private Serilog.ILogger _plain = null!;
    private Activity? _activity;

    /// <summary>The configuration the host is built with.</summary>
    public enum PipelineScenario
    {
        /// <summary>No option changed.</summary>
        Default,

        /// <summary>Five redaction patterns, applied to the request path and to every string property.</summary>
        Redaction,

        /// <summary>Three category floors and three deny-list prefixes: the two set here and the default Entity Framework Core entry, which the configuration binder keeps and appends to.</summary>
        FloorsAndNeverStepUp,

        /// <summary>A consumer sink on the Verbose root through the <c>configure</c> hook, which keeps every root enricher running on every event (ADR 0026): the cost of the pipeline without the enrichment gate.</summary>
        ConsumerRootSink,
    }

    /// <summary>The configuration the host is built with.</summary>
    [Params(PipelineScenario.Default, PipelineScenario.Redaction, PipelineScenario.FloorsAndNeverStepUp, PipelineScenario.ConsumerRootSink)]
    public PipelineScenario Scenario { get; set; }

    /// <summary>Whether an <see cref="Activity"/> is current, so every event is keyed to a trace.</summary>
    [Params(false, true)]
    public bool InActivity { get; set; }

    /// <summary>Builds the host and checks what happens to a Debug, an Information and a Warning event.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _activity = InActivity ? BenchmarkFixtures.StartTrace() : null;
        _host = BenchmarkFixtures.BuildHost(ScenarioSettings.GetValueOrDefault(Scenario, []), ScenarioHooks.GetValueOrDefault(Scenario));
        _melLogger = _host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(BenchmarkFixtures.OrderHandlerContext);
        _logger = _host.Services.GetRequiredService<Serilog.ILogger>().ForContext("SourceContext", BenchmarkFixtures.OrderHandlerContext);
        _plain = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new NullLogEventSink()).CreateLogger()
            .ForContext("SourceContext", BenchmarkFixtures.OrderHandlerContext);

        BenchmarkFixtures.Require(Exports(DebugDropped) == 0 && Held(DebugDropped) == 0, "a Debug event was exported or held");
        BenchmarkFixtures.Require(Exports(InformationHeld) == 0 && Held(InformationHeld) == 1, "an Information event was exported or not held");
        BenchmarkFixtures.Require(Exports(WarningExported) == 1 && Held(WarningExported) == 0, "a Warning event was not exported once");
        BenchmarkFixtures.Require(Exports(MelDebugDropped) == 0, "a Debug event through Microsoft.Extensions.Logging was exported");
    }

    /// <summary>Disposes the host and stops the activity.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _host.Dispose();
        BenchmarkFixtures.StopTrace(_activity);
    }

    /// <summary>An Information event on a plain Serilog logger over a sink that drops everything.</summary>
    [Benchmark(Baseline = true)]
    public void PlainSerilogToNullSink() => _plain.Information(BenchmarkFixtures.HandledTemplate, 42, "an ordinary value");

    /// <summary>A Debug event: below the pre-error buffer's level, so dropped; the root enrichers skip it unless a consumer root sink or <c>Serilog:AuditTo</c> sink is wired (ADR 0026).</summary>
    [Benchmark]
    public void DebugDropped() => _logger.Debug(BenchmarkFixtures.HandledTemplate, 42, "an ordinary value");

    /// <summary>A Debug event through the <c>Microsoft.Extensions.Logging</c> path (<c>ILogger.LogDebug</c>) in the same category: the application's usual call, dropped the same way.</summary>
    [Benchmark]
    public void MelDebugDropped() => _melLogger.LogDebug(BenchmarkFixtures.HandledTemplate, 42, "an ordinary value");

    /// <summary>An Information event at the base level Warning: not exported, held in the pre-error buffer.</summary>
    [Benchmark]
    public void InformationHeld() => _logger.Information(BenchmarkFixtures.HandledTemplate, 42, "an ordinary value");

    /// <summary>A Warning event at the base level Warning: exported to the output sink.</summary>
    [Benchmark]
    public void WarningExported() => _logger.Warning(BenchmarkFixtures.HandledTemplate, 42, "an ordinary value");

    private static int Exports(Action log) => BenchmarkFixtures.ExportedBy(log);

    private static long Held(Action log) => BenchmarkFixtures.CountedBy(BufferMeter, BufferedEvents, log);
}
