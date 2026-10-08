using BenchmarkDotNet.Attributes;
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lukdrasil.StepUpLogging.Benchmarks;

/// <summary>
/// What an audit write costs before the disk: enriching and counting the record in
/// <see cref="AuditLogger{T}"/> over a sink that stores nothing, and turning the enriched record into the
/// bytes the spool writes. The fsync that follows is measured by the Disk benchmarks.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Audit")]
public class AuditBenchmarks
{
    private readonly NullAuditSink _sink = new();
    private AuditLogger<AuditBenchmarks> _logger = null!;
    private AuditEvent _event = null!;
    private AuditEvent _enriched = null!;

    /// <summary>Whether a request is current, so the record gets the client address and user agent.</summary>
    [Params(false, true)]
    public bool WithHttpContext { get; set; }

    /// <summary>How many entries the record carries in its <c>Data</c>.</summary>
    [Params(0, 32)]
    public int DataEntries { get; set; }

    /// <summary>Builds the logger and checks what one write adds to the record.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        _logger = new AuditLogger<AuditBenchmarks>(
            _sink,
            NullLogger<AuditBenchmarks>.Instance,
            new FixedHttpContextAccessor { HttpContext = WithHttpContext ? BenchmarkFixtures.Request(headerCount: 8) : null },
            Options.Create(new StepUpLoggingOptions()),
            BenchmarkFixtures.SamplePatterns());
        _event = SpoolBenchmarkSupport.AuditedOperation() with { Data = BenchmarkFixtures.AuditData(DataEntries) };

        await AuditToNullSink();
        _enriched = _sink.Last!;
        BenchmarkFixtures.Require((_enriched.SourceIp is not null) == WithHttpContext, "the record's client address does not follow the request");
        BenchmarkFixtures.Require((_enriched.Data?.Count ?? 0) == DataEntries, "the record does not carry the data entries");
        BenchmarkFixtures.Require(SerializeForSpool() > 0, "the record serialized to nothing");
    }

    /// <summary>One audit write to a sink that stores nothing.</summary>
    [Benchmark]
    public ValueTask AuditToNullSink() => _logger.AuditAsync(_event);

    /// <summary>The enriched record as the spool's bytes: the payload JSON, then the envelope JSON; returns the envelope's size.</summary>
    [Benchmark]
    public int SerializeForSpool()
    {
        var payload = new SpoolPayload { ModuleName = "benchmarks", Version = "1.0.0", AuditEvent = _enriched }.ToUtf8Bytes();
        var record = SpoolWriter.Prepare(new SpoolEnvelope { EventId = _enriched.EventId, CreatedUtc = _enriched.TimestampUtc, Payload = payload });
        return record.Contents.Length;
    }

    private sealed class FixedHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
