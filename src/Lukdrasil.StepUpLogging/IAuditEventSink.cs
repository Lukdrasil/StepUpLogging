namespace Lukdrasil.StepUpLogging;

/// <summary>
/// What a sink did with the record it was handed. There is deliberately no zero member: a
/// mechanically ported sink returning <c>default</c> is a contract violation the library reports,
/// not a silent claim that the record was stored — or that it was lost.
/// </summary>
public enum AuditWriteResult
{
    /// <summary>
    /// The record reached the destination and is durable there. Returning this for a record the
    /// sink discarded is the one thing a sink author must never do: it erases the operator's only
    /// signal that audit is being lost.
    /// </summary>
    Stored = 1,

    /// <summary>
    /// The sink deliberately discarded the record, e.g. under back-pressure — the record is gone,
    /// and no retry will bring it back. The library counts it on <c>audit_events_dropped_total</c>
    /// and writes no companion log for it.
    /// </summary>
    Dropped = 2
}

/// <summary>
/// A durable destination for <see cref="AuditEvent"/> records, owned and implemented by the
/// consumer (e.g. writing to their own database, sharing their own transaction).
/// </summary>
public interface IAuditEventSink
{
    /// <summary>
    /// Writes <paramref name="auditEvent"/> to the destination and reports what became of it:
    /// <see cref="AuditWriteResult.Stored"/> once it is durable, or
    /// <see cref="AuditWriteResult.Dropped"/> if the sink deliberately discarded it. Exceptions
    /// propagate to the caller; there is no built-in retry.
    /// </summary>
    /// <returns>
    /// <see cref="AuditWriteResult.Dropped"/> is the sanctioned way to say "not stored" — the one
    /// answer that keeps a discarded record visible to operators. Do not reach for
    /// <see cref="AuditWriteResult.Stored"/> as the reflexive return value: it asserts the record
    /// exists, and a sink that says so about a record it threw away has hidden the loss for good.
    /// </returns>
    /// <remarks>
    /// There is deliberately no <see cref="System.Threading.CancellationToken"/> parameter, and
    /// none should be added: the natural token for a caller to pass would be
    /// <c>HttpContext.RequestAborted</c>, which would let a disconnecting client cancel — and so
    /// erase — its own audit trail. An audit write that has begun must run to completion.
    /// </remarks>
    ValueTask<AuditWriteResult> WriteAsync(AuditEvent auditEvent);
}
