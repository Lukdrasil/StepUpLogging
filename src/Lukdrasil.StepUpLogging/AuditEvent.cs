namespace Lukdrasil.StepUpLogging;

/// <summary>The result of the audited action.</summary>
public enum AuditOutcome
{
    /// <summary>The action completed as intended.</summary>
    Success,

    /// <summary>The action was attempted but did not complete.</summary>
    Failure,

    /// <summary>The action was refused, typically by an authorization check.</summary>
    Denied
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

    /// <summary>The outcome of the action.</summary>
    public required AuditOutcome Outcome { get; init; }

    /// <summary>The kind of actor identified by <see cref="ActorId"/>. Defaults to <c>"user"</c>.</summary>
    public string ActorType { get; init; } = "user";

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

    /// <summary>The UTC time the action was audited. Stamped by the library at call time.</summary>
    public DateTimeOffset TimestampUtc { get; init; }

    /// <summary>The W3C trace identifier of the enclosing activity, if any. Stamped by the library.</summary>
    public string? TraceId { get; init; }

    /// <summary>The W3C span identifier of the enclosing activity, if any. Stamped by the library.</summary>
    public string? SpanId { get; init; }

    /// <summary>The client IP address of the originating request, if any. Stamped by the library.</summary>
    public string? SourceIp { get; init; }

    /// <summary>The redacted <c>User-Agent</c> header of the originating request, if any. Stamped by the library.</summary>
    public string? UserAgent { get; init; }

    /// <summary>Creates an <see cref="AuditEvent"/> with <see cref="Outcome"/> set to <see cref="AuditOutcome.Success"/>.</summary>
    public static AuditEvent Success(string action, string actorId) =>
        new() { Action = action, ActorId = actorId, Outcome = AuditOutcome.Success };

    /// <summary>Creates an <see cref="AuditEvent"/> with <see cref="Outcome"/> set to <see cref="AuditOutcome.Failure"/>.</summary>
    public static AuditEvent Failure(string action, string actorId) =>
        new() { Action = action, ActorId = actorId, Outcome = AuditOutcome.Failure };

    /// <summary>Creates an <see cref="AuditEvent"/> with <see cref="Outcome"/> set to <see cref="AuditOutcome.Denied"/>.</summary>
    public static AuditEvent Denied(string action, string actorId) =>
        new() { Action = action, ActorId = actorId, Outcome = AuditOutcome.Denied };
}
