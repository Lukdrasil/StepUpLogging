namespace Lukdrasil.StepUpLogging;

/// <summary>
/// The result of the audited action. Consumers persist this as an integer in their own audit
/// store, so these numeric values are part of the contract and must never be renumbered or
/// reordered — add new members with new, higher values instead.
/// </summary>
public enum AuditOutcome
{
    /// <summary>The action completed as intended.</summary>
    Success = 0,

    /// <summary>The action was attempted but did not complete.</summary>
    Failure = 1,

    /// <summary>The action was refused, typically by an authorization check.</summary>
    Denied = 2
}

/// <summary>
/// An immutable audit record describing who did what, to what, and with what outcome. Built via
/// <see cref="Success"/>, <see cref="Failure"/>, or <see cref="Denied"/> and refined with
/// <c>with</c> expressions.
/// </summary>
public sealed record AuditEvent
{
    /// <summary>The action performed, e.g. <c>"order.cancel"</c>.</summary>
    public required string Action { get; init; }

    /// <summary>The identifier of the actor who performed the action.</summary>
    public required string ActorId { get; init; }

    /// <summary>
    /// The kind of actor identified by <see cref="ActorId"/>, e.g. <c>"user"</c>, <c>"service"</c>,
    /// or <c>"api-key"</c>. Required, and deliberately without a default: an audit record is
    /// append-only, so a guessed actor kind is a permanent false assertion about who acted.
    /// </summary>
    public required string ActorType { get; init; }

    /// <summary>The outcome of the action.</summary>
    public required AuditOutcome Outcome { get; init; }

    /// <summary>The identifier of the party the actor was acting on behalf of, if any.</summary>
    public string? OnBehalfOfId { get; init; }

    /// <summary>The tenant the action was performed within, if applicable.</summary>
    public string? TenantId { get; init; }

    /// <summary>The type of the entity the action targeted, if any.</summary>
    public string? TargetType { get; init; }

    /// <summary>The identifier of the entity the action targeted, if any.</summary>
    public string? TargetId { get; init; }

    /// <summary>A human-readable explanation of the outcome, if any.</summary>
    public string? Reason { get; init; }

    /// <summary>Additional caller-supplied context, opaque to the library.</summary>
    public IReadOnlyDictionary<string, object?>? Data { get; init; }

    /// <summary>
    /// The state of the target before the action, if the call site records it. Caller-supplied and,
    /// like <see cref="Data"/>, opaque to the library and never redacted.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? OldValues { get; init; }

    /// <summary>
    /// The state of the target after the action, if the call site records it. Caller-supplied and,
    /// like <see cref="Data"/>, opaque to the library and never redacted.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? NewValues { get; init; }

    /// <summary>
    /// The identity of this audit record. Owned by the library: a UUIDv7 is stamped on every call,
    /// so any value a caller sets here is overwritten before the record reaches the sink. Delivery
    /// to an audit store is at-least-once, and this is what the receiver deduplicates on.
    /// </summary>
    public Guid EventId { get; init; }

    /// <summary>
    /// The UTC time the action was audited. Owned by the library: stamped on every call, so any
    /// value a caller sets here is overwritten before the record reaches the sink.
    /// </summary>
    public DateTimeOffset TimestampUtc { get; init; }

    /// <summary>
    /// The W3C trace identifier of the enclosing activity, if any. Owned by the library: taken
    /// from the ambient activity on every call, so a caller-set value is overwritten.
    /// </summary>
    public string? TraceId { get; init; }

    /// <summary>
    /// The W3C span identifier of the enclosing activity, if any. Owned by the library: taken
    /// from the ambient activity on every call, so a caller-set value is overwritten.
    /// </summary>
    public string? SpanId { get; init; }

    /// <summary>
    /// The client IP address of the originating request, if any. Owned by the library: taken from
    /// the current request on every call, so a caller-set value is overwritten.
    /// </summary>
    public string? SourceIp { get; init; }

    /// <summary>
    /// The redacted <c>User-Agent</c> header of the originating request, if any. Owned by the
    /// library: taken from the current request and redacted on every call, so a caller-set value
    /// is overwritten.
    /// </summary>
    public string? UserAgent { get; init; }

    /// <summary>
    /// Creates a <see cref="AuditOutcome.Success"/> event. Everything beyond the three parameters
    /// stays at its default — add context with a <c>with</c> expression.
    /// </summary>
    /// <param name="action">The action performed, e.g. <c>"order.cancel"</c>.</param>
    /// <param name="actorId">The identifier of the actor who performed the action.</param>
    /// <param name="actorType">
    /// The kind of actor <paramref name="actorId"/> identifies, e.g. <c>"user"</c>,
    /// <c>"service"</c>, or <c>"api-key"</c>.
    /// </param>
    /// <remarks>
    /// All three parameters are strings, so nothing but this order — <c>action, actorId,
    /// actorType</c> — stops two of them being transposed at a call site: the compiler cannot
    /// catch it, and the resulting audit record is a permanent, plausible-looking lie about who
    /// did what. Read the argument order back before moving on.
    /// </remarks>
    public static AuditEvent Success(string action, string actorId, string actorType) =>
        new() { Action = action, ActorId = actorId, ActorType = actorType, Outcome = AuditOutcome.Success };

    /// <summary>
    /// Creates a <see cref="AuditOutcome.Failure"/> event. Everything beyond the three parameters
    /// stays at its default — add context with a <c>with</c> expression.
    /// </summary>
    /// <param name="action">The action attempted, e.g. <c>"payment.capture"</c>.</param>
    /// <param name="actorId">The identifier of the actor who attempted the action.</param>
    /// <param name="actorType">
    /// The kind of actor <paramref name="actorId"/> identifies, e.g. <c>"user"</c>,
    /// <c>"service"</c>, or <c>"api-key"</c>.
    /// </param>
    /// <remarks>
    /// All three parameters are strings, so nothing but this order — <c>action, actorId,
    /// actorType</c> — stops two of them being transposed at a call site: the compiler cannot
    /// catch it, and the resulting audit record is a permanent, plausible-looking lie about who
    /// did what. Read the argument order back before moving on.
    /// </remarks>
    public static AuditEvent Failure(string action, string actorId, string actorType) =>
        new() { Action = action, ActorId = actorId, ActorType = actorType, Outcome = AuditOutcome.Failure };

    /// <summary>
    /// Creates a <see cref="AuditOutcome.Denied"/> event. Everything beyond the three parameters
    /// stays at its default — add context with a <c>with</c> expression.
    /// </summary>
    /// <param name="action">The action refused, e.g. <c>"report.export"</c>.</param>
    /// <param name="actorId">The identifier of the actor the action was refused to.</param>
    /// <param name="actorType">
    /// The kind of actor <paramref name="actorId"/> identifies — for a denial commonly an
    /// unauthenticated or non-human caller, e.g. <c>"anonymous"</c> or <c>"service"</c>.
    /// </param>
    /// <remarks>
    /// All three parameters are strings, so nothing but this order — <c>action, actorId,
    /// actorType</c> — stops two of them being transposed at a call site: the compiler cannot
    /// catch it, and the resulting audit record is a permanent, plausible-looking lie about who
    /// did what. Read the argument order back before moving on.
    /// </remarks>
    public static AuditEvent Denied(string action, string actorId, string actorType) =>
        new() { Action = action, ActorId = actorId, ActorType = actorType, Outcome = AuditOutcome.Denied };
}
