namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// Told about each record the audit endpoint permanently rejected on its own, after the record has
/// been dead-lettered, so a consumer can react (for example refresh a sealing key) before the next
/// record is sealed.
/// </summary>
/// <remarks>
/// Informs only: the record is already dead-lettered and nothing here can retry or re-seal it, since
/// the spool holds sealed bytes. Register as a thread-safe singleton; the drain worker is a hosted
/// singleton, so a scoped registration would be a captive dependency. The call is bounded by
/// <see cref="EncryptedSpoolOptions.DeliveryTimeout"/>, and an exception it throws is logged and
/// never stops the drain cycle.
/// </remarks>
public interface IAuditRejectionObserver
{
    /// <summary>Called once for a record the endpoint rejected on its own, after it was dead-lettered.</summary>
    /// <param name="rejection">The status code and problem code the endpoint answered with.</param>
    /// <param name="cancellationToken">Signalled when the observer ran past its time limit or the host is stopping.</param>
    ValueTask OnRejectedAsync(AuditRejection rejection, CancellationToken cancellationToken);
}
