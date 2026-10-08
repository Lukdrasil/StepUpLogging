using BenchmarkDotNet.Attributes;
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

namespace Lukdrasil.StepUpLogging.Benchmarks;

/// <summary>
/// What a write costs once the spool sits at its 100 000-file cap and every record is dropped
/// (issue #69). The baseline is the scan every dropped write paid before: a measure of the whole
/// directory.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Disk")]
public class FullSpoolDropBenchmarks
{
    private const int Cap = 100_000;

    private string _spoolDirectory = null!;
    private SpoolCapacity _capacity = null!;
    private EncryptedSpoolAuditSink _sink = null!;

    /// <summary>Fills a spool to its cap and lets the first write measure it.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        _spoolDirectory = SpoolBenchmarkSupport.FreshSpoolDirectory(nameof(FullSpoolDropBenchmarks));
        var options = SpoolBenchmarkSupport.OptionsFor(_spoolDirectory);
        options.SpoolMaxEntries = Cap;

        await SpoolBenchmarkSupport.FillWithRecordsAsync(_spoolDirectory, Cap);
        _capacity = new SpoolCapacity(options);
        _sink = SpoolBenchmarkSupport.SinkFor(options);

        // The first write measures the spool; every one after it is what is benchmarked.
        var first = await _sink.WriteAsync(SpoolBenchmarkSupport.AuditedOperation());
        SpoolBenchmarkSupport.RequireDropped(first);
    }

    /// <summary>Disposes the sink and removes its spool.</summary>
    [GlobalCleanup]
    public void Cleanup() => SpoolBenchmarkSupport.Discard(_sink, _spoolDirectory);

    /// <summary>The cost of learning how full the spool is by reading the directory, as every write did.</summary>
    [Benchmark(Baseline = true)]
    public int MeasureTheSpool() => _capacity.Measure().Records;

    /// <summary>The cost of a write the full spool refuses.</summary>
    [Benchmark]
    public ValueTask<AuditWriteResult> DroppedWrite() => _sink.WriteAsync(SpoolBenchmarkSupport.AuditedOperation());
}
