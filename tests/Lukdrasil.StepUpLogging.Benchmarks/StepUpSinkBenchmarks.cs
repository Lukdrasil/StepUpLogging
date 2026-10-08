using BenchmarkDotNet.Attributes;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Benchmarks;

/// <summary>
/// What <see cref="StepUpSink"/> costs per event it drops below the switch and per event it exports,
/// with and without the deny-list and category floors. The sink is called directly, with prebuilt
/// events, so the numbers are the sink's decision and nothing around it.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Logging")]
public class StepUpSinkBenchmarks
{
    private readonly NullLogEventSink _exported = new();
    private StepUpSink _sink = null!;
    private LogEvent _belowSwitch = null!;
    private LogEvent _atSwitch = null!;

    /// <summary>Whether the sink also carries the deny-list and the category floors.</summary>
    [Params(false, true)]
    public bool WithCategoryRules { get; set; }

    /// <summary>Builds the sink over a switch at Warning and checks which of the two events it exports.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var output = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_exported).CreateLogger();
        _sink = new StepUpSink(
            output,
            new LoggingLevelSwitch(LogEventLevel.Warning),
            LogEventLevel.Warning,
            WithCategoryRules ? ["Microsoft.EntityFrameworkCore.Database.Command"] : [],
            heldBackBuffer: null,
            WithCategoryRules ? new CategoryFloorMap(CategoryFloors(), []) : null);
        _belowSwitch = BenchmarkFixtures.HandledEvent(LogEventLevel.Debug, extra: 4);
        _atSwitch = BenchmarkFixtures.HandledEvent(LogEventLevel.Warning, extra: 4);

        EmitBelowSwitch();
        BenchmarkFixtures.Require(_exported.Received == 0, "an event below the switch reached the output");
        EmitExported();
        BenchmarkFixtures.Require(_exported.Received == 1, "an event at the switch did not reach the output once");
    }

    /// <summary>Disposes the sink and the output logger under it.</summary>
    [GlobalCleanup]
    public void Cleanup() => _sink.Dispose();

    /// <summary>An event below the switch: decided and dropped.</summary>
    [Benchmark]
    public void EmitBelowSwitch() => _sink.Emit(_belowSwitch);

    /// <summary>An event at the switch: decided and written to the output logger.</summary>
    [Benchmark]
    public void EmitExported() => _sink.Emit(_atSwitch);

    private static Dictionary<string, LogEventLevel> CategoryFloors() => new(StringComparer.Ordinal)
    {
        ["Microsoft.AspNetCore"] = LogEventLevel.Warning,
        ["System.Net.Http"] = LogEventLevel.Warning,
        ["MyApp"] = LogEventLevel.Warning,
    };
}
