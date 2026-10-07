using BenchmarkDotNet.Attributes;
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

namespace Lukdrasil.StepUpLogging.Benchmarks;

/// <summary>
/// What a write costs once the spool sits at its 100 000-file cap and every record is dropped
/// (issue #69). The baseline is the scan every dropped write paid before: a measure of the whole
/// directory.
/// </summary>
[MemoryDiagnoser]
public class FullSpoolDropBenchmarks
{
    private const int Cap = 100_000;

    private string _spoolDirectory = null!;
    private SpoolCapacity _capacity = null!;
    private EncryptedSpoolAuditSink _sink = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _spoolDirectory = SpoolBenchmarkSupport.FreshSpoolDirectory(nameof(FullSpoolDropBenchmarks));
        var options = SpoolBenchmarkSupport.OptionsFor(_spoolDirectory);
        options.SpoolMaxEntries = Cap;

        var writer = new SpoolWriter(_spoolDirectory);
        for (var i = 0; i < Cap; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(_spoolDirectory, $"{i:D8}.env"), "{}"u8.ToArray());
        }

        _capacity = new SpoolCapacity(options);
        _sink = SpoolBenchmarkSupport.SinkFor(options, writer, SpoolBenchmarkSupport.TrackerFor(options));

        // The first write measures the spool; every one after it is what is benchmarked.
        if (await _sink.WriteAsync(SpoolBenchmarkSupport.AuditedOperation()) != AuditWriteResult.Dropped)
        {
            throw new InvalidOperationException("the spool is not at its cap");
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _sink.Dispose();
        SpoolBenchmarkSupport.DeleteWithSiblings(_spoolDirectory);
    }

    /// <summary>The cost of learning how full the spool is by reading the directory, as every write did.</summary>
    [Benchmark(Baseline = true)]
    public int MeasureTheSpool() => _capacity.Measure().Records;

    /// <summary>The cost of a write the full spool refuses.</summary>
    [Benchmark]
    public ValueTask<AuditWriteResult> DroppedWrite() => _sink.WriteAsync(SpoolBenchmarkSupport.AuditedOperation());
}
