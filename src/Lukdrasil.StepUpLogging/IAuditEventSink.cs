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
    ValueTask WriteAsync(AuditEvent auditEvent);
}
