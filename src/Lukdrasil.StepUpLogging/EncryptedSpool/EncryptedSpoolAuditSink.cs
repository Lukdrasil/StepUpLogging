using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// The audit sink that serializes each record, hands it to the consumer's
/// <see cref="IAuditPayloadEncryptor"/>, and returns only once the encrypted result is durably
/// spooled for the drain worker to deliver (ADR 0020 D4). A full spool is the one condition under
/// which it reports <see cref="AuditWriteResult.Dropped"/>; every other failure — a record that
/// will not serialize, an encryptor that throws, a disk that will not take the write — propagates
/// to the business call site (ADR 0016 D2, ADR 0020 D6).
/// </summary>
internal sealed class EncryptedSpoolAuditSink(
    IOptions<EncryptedSpoolOptions> options,
    IAuditPayloadEncryptor encryptor,
    ILogger<EncryptedSpoolAuditSink> logger) : IAuditEventSink, IDisposable
{
    private readonly EncryptedSpoolOptions _options = options.Value;
    private readonly SpoolWriter _writer = new(options.Value.SpoolDirectory);
    private readonly SpoolCapacity _capacity = new(options.Value);
    private readonly SemaphoreSlim _spoolGate = new(1, 1);

    /// <summary>Guarded by <see cref="_spoolGate"/>, so the warning is logged on crossing the threshold, once.</summary>
    private bool _wasAtWarnThreshold;

    /// <inheritdoc />
    public async ValueTask<AuditWriteResult> WriteAsync(AuditEvent auditEvent)
    {
        var payload = SerializeWithinCap(auditEvent);
        var blob = await encryptor.EncryptAsync(payload).ConfigureAwait(false);
        var envelope = new SpoolEnvelope
        {
            EventId = auditEvent.EventId,
            CreatedUtc = auditEvent.TimestampUtc,
            Payload = blob
        };

        // Measuring and writing under one gate is what makes the cap hold: concurrent writers that
        // each measured a spool one record short of full would otherwise all write into it. It
        // costs the writes their concurrency, against an fsync that already dominates them
        // (ADR 0020 consequences) and an audit volume measured in mutating operations.
        await _spoolGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var usage = _capacity.Measure();
            ReportReachingWarnThreshold(usage);

            if (usage.IsFull)
            {
                return Drop(auditEvent, usage);
            }

            await _writer.WriteAsync(envelope).ConfigureAwait(false);
            return AuditWriteResult.Stored;
        }
        finally
        {
            _spoolGate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _spoolGate.Dispose();

    /// <summary>
    /// Serializes the record before anything else touches it, so a value that cannot be written or
    /// a runaway <see cref="AuditEvent.Data"/> fails here — attributed to the call site — rather
    /// than later, as an encryption or a disk error (ADR 0020 D4).
    /// </summary>
    private byte[] SerializeWithinCap(AuditEvent auditEvent)
    {
        var payload = new SpoolPayload
        {
            ModuleName = _options.ModuleName,
            Version = _options.Version,
            AuditEvent = auditEvent
        }.ToUtf8Bytes();

        if (payload.Length > _options.MaxPayloadBytes)
        {
            throw new ArgumentException(
                $"Audit record {auditEvent.EventId} serializes to {payload.Length} bytes, over the " +
                $"{_options.MaxPayloadBytes}-byte {nameof(EncryptedSpoolOptions)}.{nameof(EncryptedSpoolOptions.MaxPayloadBytes)} " +
                "cap. It was neither encrypted nor spooled: audit what changed rather than whole entities.",
                nameof(auditEvent));
        }

        return payload;
    }

    private AuditWriteResult Drop(AuditEvent auditEvent, SpoolUsage usage)
    {
        EncryptedSpoolMetrics.RejectedFullCounter.Add(1);

        // Critical, and once per lost record: this is deliberate, bounded, visible loss of an audit
        // record, and the only trace of it left is here and on the counter (ADR 0020 D6).
        logger.LogCritical(
            "Audit record {EventId} was dropped: the spool at {SpoolDirectory} is full with {SpooledRecords} of {SpoolMaxEntries} records ({SpooledBytes} of {SpoolMaxBytes} bytes). The record is lost and no retry will bring it back — the spool drains only as fast as the audit endpoint accepts it.",
            auditEvent.EventId, _options.SpoolDirectory, usage.Records, _options.SpoolMaxEntries, usage.Bytes, _options.SpoolMaxBytes);

        return AuditWriteResult.Dropped;
    }

    /// <summary>
    /// Logs the crossing into the warn zone rather than every write above it, which past half a cap
    /// would be one ERROR per audited operation.
    /// </summary>
    private void ReportReachingWarnThreshold(SpoolUsage usage)
    {
        if (usage.IsAtWarnThreshold && !_wasAtWarnThreshold)
        {
            logger.LogError(
                "The audit spool at {SpoolDirectory} holds {SpooledRecords} of {SpoolMaxEntries} records ({SpooledBytes} of {SpoolMaxBytes} bytes), at least half its cap. Spooled records are never rotated out, so once it is full the new audit records are the ones dropped.",
                _options.SpoolDirectory, usage.Records, _options.SpoolMaxEntries, usage.Bytes, _options.SpoolMaxBytes);
        }

        _wasAtWarnThreshold = usage.IsAtWarnThreshold;
    }
}

/// <summary>The encrypted spool's own instruments, on the audit meter consumers already export.</summary>
internal static class EncryptedSpoolMetrics
{
    internal static readonly Counter<long> RejectedFullCounter = AuditMetrics.Meter.CreateCounter<long>(
        "audit_spool_rejected_full_total",
        "count",
        "Number of audit records dropped because the spool was at its cap");
}
