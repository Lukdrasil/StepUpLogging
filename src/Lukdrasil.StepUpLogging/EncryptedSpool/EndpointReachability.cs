namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// What the drain worker's own delivery attempts say about the audit endpoint. There is nothing
/// else to derive it from — the worker is the only thing in this library that talks to the
/// endpoint — so a run of failed attempts is the signal, and the first attempt that gets through
/// ends it (ADR 0019 D4).
/// </summary>
/// <remarks>
/// Written by the drain worker and read by the health check, on different threads.
/// </remarks>
internal sealed class EndpointReachability
{
    /// <summary>
    /// How many attempts in a row have to fail before the endpoint counts as unreachable. One
    /// failed attempt is a dropped connection or a receiver restarting; a run of them is an
    /// endpoint that is not taking audit records.
    /// </summary>
    private const int FailedAttemptsBeforeUnreachable = 3;

    private int _consecutiveFailures;

    /// <summary>Failed delivery attempts since the last one that reached the endpoint.</summary>
    public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

    /// <summary>True while the endpoint has failed enough attempts in a row to be reported as unreachable.</summary>
    public bool IsUnreachable => ConsecutiveFailures >= FailedAttemptsBeforeUnreachable;

    /// <summary>Records a delivery attempt the endpoint did not take the record on.</summary>
    public void AttemptFailed() => Interlocked.Increment(ref _consecutiveFailures);

    /// <summary>Records a delivery attempt the endpoint answered definitively, stored or rejected.</summary>
    public void EndpointAnswered() => Interlocked.Exchange(ref _consecutiveFailures, 0);
}
