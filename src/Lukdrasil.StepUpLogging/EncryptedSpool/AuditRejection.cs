namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// What the audit endpoint said when it permanently rejected a single spooled record.
/// </summary>
/// <param name="StatusCode">The HTTP status code of the rejection.</param>
/// <param name="ProblemCode">
/// The top-level <c>code</c> of an <c>application/problem+json</c> body, or <see langword="null"/>
/// when the body was absent, not a problem document, malformed, too large, or carried no string code.
/// </param>
public readonly record struct AuditRejection(int StatusCode, string? ProblemCode);
