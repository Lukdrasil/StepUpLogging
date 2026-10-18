using System.Collections.Concurrent;

using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

namespace AuditSpoolApi;

/// <summary>
/// Refreshes the sealing key when the endpoint rejects a record with <c>422 sealing.unknown_key</c>,
/// so the records sealed afterwards are deliverable. The rejected record stays dead-lettered.
/// </summary>
public sealed class RefreshKeyOnUnknownKey(DemoKeyRing keyRing, ILogger<RefreshKeyOnUnknownKey> logger) : IAuditRejectionObserver
{
    private readonly ConcurrentQueue<AuditRejection> _seen = new();

    public IEnumerable<AuditRejection> Seen => _seen;

    public ValueTask OnRejectedAsync(AuditRejection rejection, CancellationToken cancellationToken)
    {
        _seen.Enqueue(rejection);
        if (rejection is { StatusCode: StatusCodes.Status422UnprocessableEntity, ProblemCode: "sealing.unknown_key" })
        {
            keyRing.Refresh();
            logger.LogWarning("The audit endpoint does not know the sealing key, refreshed it to {KeyId}", keyRing.CurrentKeyId);
        }

        return ValueTask.CompletedTask;
    }
}
