using Microsoft.Extensions.Logging;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Writes <see cref="AuditEvent"/> records to the registered <see cref="IAuditEventSink"/>,
/// enriching them with request and trace context. The type parameter <typeparamref name="T"/>
/// scopes the companion log's category, matching <see cref="ILogger{TCategoryName}"/>.
/// </summary>
public interface IAuditLogger<T>
{
    /// <summary>Enriches and writes <paramref name="auditEvent"/> to the configured sink.</summary>
    /// <remarks>
    /// There is deliberately no <see cref="System.Threading.CancellationToken"/> parameter — see
    /// <see cref="IAuditEventSink.WriteAsync"/> for why an audit write must not be cancellable.
    /// </remarks>
    ValueTask AuditAsync(AuditEvent auditEvent);

    /// <summary>
    /// Enriches and writes <paramref name="auditEvent"/> to the configured sink, then invokes
    /// <paramref name="log"/> with a companion <see cref="ILogger"/> whose event bypasses the
    /// step-up level switch. <paramref name="log"/> is invoked only when the sink reports
    /// <see cref="AuditWriteResult.Stored"/>: a sink that throws, or that reports
    /// <see cref="AuditWriteResult.Dropped"/> because it discarded the record, gets no companion
    /// log — by design, so a log line can never assert an action no audit record backs.
    /// </summary>
    /// <remarks>
    /// The companion log is handed over as an <see cref="Action{T}"/> over <see cref="ILogger"/>
    /// rather than as a message template plus arguments so that callers keep using their
    /// <c>[LoggerMessage]</c> source-generated methods, with the compile-time template checking
    /// and allocation-free path those provide. Do not add a
    /// <c>(string template, params object?[] args)</c> convenience overload: it would defeat that.
    /// </remarks>
    ValueTask AuditAsync(AuditEvent auditEvent, Action<ILogger> log);
}
