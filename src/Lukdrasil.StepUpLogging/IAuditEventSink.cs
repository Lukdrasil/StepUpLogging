namespace Lukdrasil.StepUpLogging;

/// <summary>
/// A durable destination for <see cref="AuditEvent"/> records, owned and implemented by the
/// consumer (e.g. writing to their own database, sharing their own transaction).
/// </summary>
public interface IAuditEventSink
{
    /// <summary>
    /// Writes <paramref name="auditEvent"/> to the destination. Exceptions propagate to the
    /// caller; there is no built-in retry or failure-swallowing.
    /// </summary>
    /// <remarks>
    /// There is deliberately no <see cref="System.Threading.CancellationToken"/> parameter, and
    /// none should be added: the natural token for a caller to pass would be
    /// <c>HttpContext.RequestAborted</c>, which would let a disconnecting client cancel — and so
    /// erase — its own audit trail. An audit write that has begun must run to completion.
    /// </remarks>
    ValueTask WriteAsync(AuditEvent auditEvent);
}
