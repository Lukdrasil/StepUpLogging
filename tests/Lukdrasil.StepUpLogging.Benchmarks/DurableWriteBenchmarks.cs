using BenchmarkDotNet.Attributes;
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

namespace Lukdrasil.StepUpLogging.Benchmarks;

/// <summary>
/// Durable writes per second with one writer and with eight (issue #69). Every write ends in an
/// fsync, so what this shows is whether concurrent writers overlap in it or queue behind each
/// other. The spool lives under the build output, or <see cref="SpoolBenchmarkSupport.SpoolRootVariable"/>
/// when set — never on a RAM disk, where there is no fsync to wait for.
/// </summary>
[MemoryDiagnoser]
public class DurableWriteBenchmarks
{
    private const int RecordCount = 64;

    private string _spoolDirectory = null!;
    private EncryptedSpoolAuditSink _sink = null!;

    /// <summary>How many writers share the records between them.</summary>
    [Params(1, 8)]
    public int Writers { get; set; }

    /// <summary>Opens a sink over a spool no cap can fill during the run.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _spoolDirectory = SpoolBenchmarkSupport.FreshSpoolDirectory(nameof(DurableWriteBenchmarks));
        var options = SpoolBenchmarkSupport.OptionsFor(_spoolDirectory);
        options.SpoolMaxEntries = int.MaxValue;
        options.SpoolMaxBytes = long.MaxValue;

        _sink = SpoolBenchmarkSupport.SinkFor(options);
    }

    /// <summary>Disposes the sink and removes its spool.</summary>
    [GlobalCleanup]
    public void Cleanup() => SpoolBenchmarkSupport.Discard(_sink, _spoolDirectory);

    /// <summary>Writes <see cref="RecordCount"/> records split across the writers; reported per record, so its inverse is writes per second.</summary>
    [Benchmark(OperationsPerInvoke = RecordCount)]
    public Task WriteRecords() =>
        Task.WhenAll(Enumerable.Range(0, Writers).Select(_ => Task.Run(WriteOwnShareAsync)));

    private async Task WriteOwnShareAsync()
    {
        for (var i = 0; i < RecordCount / Writers; i++)
        {
            await _sink.WriteAsync(SpoolBenchmarkSupport.AuditedOperation());
        }
    }
}
