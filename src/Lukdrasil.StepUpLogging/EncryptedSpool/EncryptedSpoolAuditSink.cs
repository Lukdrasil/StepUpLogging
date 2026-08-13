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
    SpoolWriter writer,
    IAuditPayloadEncryptor encryptor,
    ILogger<EncryptedSpoolAuditSink> logger) : IAuditEventSink, IDisposable
{
    private readonly EncryptedSpoolOptions _options = options.Value;
    private readonly SpoolUsageTracker _usage = new(new SpoolCapacity(options.Value));

    // A semaphore rather than a lock: the section it guards awaits the spool write, and no lock
    // can be held across an await.
    private readonly SemaphoreSlim _spoolGate = new(1, 1);

    /// <summary>Guarded by <see cref="_spoolGate"/>, so the warning is logged on crossing the threshold, once.</summary>
    private bool _wasAtWarnThreshold;

    /// <inheritdoc />
    public async ValueTask<AuditWriteResult> WriteAsync(AuditEvent auditEvent)
    {
        var payload = SerializeWithinCap(auditEvent);

        // Encrypting before the spool's cap is consulted costs a wasted port call for a record that
        // then turns out to be dropped. The other order costs audit records: the verdict would be
        // taken outside the gate, or the gate would be held across a consumer's port call, and a
        // record dropped for room the drain worker freed a microsecond later is gone for good.
        var encryptedPayload = await encryptor.EncryptAsync(payload).ConfigureAwait(false);
        var envelope = new SpoolEnvelope
        {
            EventId = auditEvent.EventId,
            CreatedUtc = auditEvent.TimestampUtc,
            Payload = encryptedPayload
        };

        // Measuring and writing under one gate is what makes the cap hold: concurrent writers that
        // each measured a spool one record short of full would otherwise all write into it. It
        // costs the writes their concurrency, against an fsync that already dominates them
        // (ADR 0020 consequences) and an audit volume measured in mutating operations.
        await _spoolGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var usage = _usage.Read();
            ReportReachingWarnThreshold(usage);

            if (usage.IsFull)
            {
                return Drop(auditEvent, usage);
            }

            _usage.Recorded(await WriteToSpoolAsync(envelope).ConfigureAwait(false));
            return AuditWriteResult.Stored;
        }
        finally
        {
            _spoolGate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _spoolGate.Dispose();

    private async Task<long> WriteToSpoolAsync(SpoolEnvelope envelope)
    {
        try
        {
            return await writer.WriteAsync(envelope).ConfigureAwait(false);
        }
        catch
        {
            // A write that failed part-way leaves a `.tmp` occupying the spool until the next
            // start-up sweep, so what is on disk is no longer what this sink has added up.
            _usage.Invalidate();
            throw;
        }
    }

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
        "Number of audit records dropped because the spool was at its cap; untagged, as the outcome of each dropped record is already on audit_events_dropped_total");

    internal static readonly Counter<long> DrainedCounter = AuditMetrics.Meter.CreateCounter<long>(
        "audit_spool_drained_total",
        "count",
        "Number of spooled audit records the audit endpoint confirmed it stored, after which the spool file is deleted");

    internal static readonly Counter<long> DrainFailureCounter = AuditMetrics.Meter.CreateCounter<long>(
        "audit_spool_drain_failures_total",
        "count",
        "Number of delivery attempts that did not get a spooled audit record through — a server error, a refused connection, a timeout — after which it stays spooled and is retried");

    internal static readonly Counter<long> DeadLetteredCounter = AuditMetrics.Meter.CreateCounter<long>(
        "audit_spool_dead_lettered_total",
        "count",
        "Number of audit records moved to dead-letter/ because no retry can deliver them; every one of them is an audit record that never reached the audit store");
}
