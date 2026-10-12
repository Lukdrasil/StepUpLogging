using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// An <see cref="IAuditRejectionObserver"/> that keeps what it was told, and optionally runs
/// <paramref name="onRejected"/> so a test can make it throw or never finish.
/// </summary>
internal sealed class RecordingRejectionObserver(Func<AuditRejection, CancellationToken, ValueTask>? onRejected = null) : IAuditRejectionObserver
{
    // Locked on every side: the drain worker calls from its own thread while the test reads.
    private readonly List<AuditRejection> _rejections = [];

    public IReadOnlyList<AuditRejection> Rejections
    {
        get { lock (_rejections) { return [.. _rejections]; } }
    }

    public ValueTask OnRejectedAsync(AuditRejection rejection, CancellationToken cancellationToken)
    {
        lock (_rejections)
        {
            _rejections.Add(rejection);
        }

        return onRejected?.Invoke(rejection, cancellationToken) ?? ValueTask.CompletedTask;
    }
}
