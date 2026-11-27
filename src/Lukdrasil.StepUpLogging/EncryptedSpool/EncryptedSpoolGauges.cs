using System.Diagnostics.Metrics;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// The spool's own gauges on the <c>StepUpLogging.Audit</c> meter — <c>audit_spool_depth</c>
/// (records), <c>audit_spool_bytes</c> and <c>audit_spool_oldest_age_seconds</c> — the first two backed by the same tally the
/// write path already maintains, via <see cref="Spool.Usage"/>: a directory scan
/// happens at most once (seeding the tally the first time anything asks), never on every collection
/// interval, and the value reported carries the same tolerance the write path already accepts — only
/// ever too high, never too low, self-correcting on the next successful write (issue #22 B8). The
/// age gauge reads <see cref="Spool.OldestRecordAgeAt"/>, which the drain keeps current, so it
/// costs no scan either.
/// </summary>
/// <remarks>
/// Registered once, as a DI singleton, for the process's one <c>AddEncryptedSpoolAuditSink</c> call
/// (ADR 0016 D4 — the registration call is the switch, and B02 refuses a second sink). Instruments
/// created on a <see cref="Meter"/> have no individual <c>Dispose</c>; only the <c>Meter</c> itself
/// does, and it is shared for the process's lifetime, so there is nothing here to release early.
/// </remarks>
internal sealed class EncryptedSpoolGauges
{
    public EncryptedSpoolGauges(Spool spool, TimeProvider timeProvider)
    {
        AuditMetrics.Meter.CreateObservableGauge(
            "audit_spool_depth",
            () => (long)spool.Usage.Records,
            "count",
            "Records currently held in the spool, from the write path's cached tally (SpoolUsageTracker) rather than a fresh directory scan.");

        AuditMetrics.Meter.CreateObservableGauge(
            "audit_spool_bytes",
            () => spool.Usage.Bytes,
            "byte",
            "Bytes currently held in the spool, from the same cached tally as audit_spool_depth.");

        AuditMetrics.Meter.CreateObservableGauge(
            "audit_spool_oldest_age_seconds",
            () => (long)spool.OldestRecordAgeAt(timeProvider.GetUtcNow()).TotalSeconds,
            "s",
            "Age in whole seconds of the oldest record the drain worker is waiting on, 0 when the spool is empty. It keeps growing while delivery is stuck behind one record, however empty the spool still is.");
    }
}
