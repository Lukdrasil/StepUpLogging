using System.Diagnostics.Metrics;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// The spool's own gauges on the <c>StepUpLogging.Audit</c> meter — <c>audit_spool_depth</c>
/// (records) and <c>audit_spool_bytes</c> — backed by the same <see cref="SpoolUsageTracker"/> the
/// write path already maintains, via <see cref="SpoolUsageTracker.Snapshot"/>: a collection
/// interval never costs the spool a fresh directory scan, and the value it reports carries the same
/// tolerance the write path already accepts — only ever too high, never too low, self-correcting on
/// the next successful write (issue #22 B8).
/// </summary>
/// <remarks>
/// Registered once, as a DI singleton, for the process's one <c>AddEncryptedSpoolAuditSink</c> call
/// (ADR 0016 D4 — the registration call is the switch, and B02 refuses a second sink). Instruments
/// created on a <see cref="Meter"/> have no individual <c>Dispose</c>; only the <c>Meter</c> itself
/// does, and it is shared for the process's lifetime, so there is nothing here to release early.
/// </remarks>
internal sealed class EncryptedSpoolGauges
{
    public EncryptedSpoolGauges(SpoolUsageTracker usageTracker)
    {
        AuditMetrics.Meter.CreateObservableGauge(
            "audit_spool_depth",
            () => (long)usageTracker.Snapshot.Records,
            "count",
            "Records currently held in the spool, from the write path's cached tally (SpoolUsageTracker) rather than a fresh directory scan.");

        AuditMetrics.Meter.CreateObservableGauge(
            "audit_spool_bytes",
            () => usageTracker.Snapshot.Bytes,
            "byte",
            "Bytes currently held in the spool, from the same cached tally as audit_spool_depth.");
    }
}
