using System.Text.Json.Serialization;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// The thin outer object the spool stores: the encryptor's opaque blob, plus the identity and
/// creation instant of the record in clear, so the drain worker can order and deduplicate without
/// decrypting anything (ADR 0017 D4).
/// </summary>
internal sealed record SpoolEnvelope
{
    /// <summary>The <c>EventId</c> of the audit record this envelope carries.</summary>
    [JsonPropertyName("eventId")]
    public required Guid EventId { get; init; }

    /// <summary>The instant the audit record was created.</summary>
    [JsonPropertyName("createdUtc")]
    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>
    /// The opaque blob returned by <see cref="IAuditPayloadEncryptor.EncryptAsync"/>. The spool
    /// never interprets it.
    /// </summary>
    [JsonPropertyName("payload")]
    public required byte[] Payload { get; init; }
}
