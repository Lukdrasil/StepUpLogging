using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// What the sink encrypts: the enriched audit record, plus the producing module's name and
/// version.
/// </summary>
internal sealed record SpoolPayload
{
    /// <summary>The name of the module that produced <see cref="AuditEvent"/>.</summary>
    [JsonPropertyName("moduleName")]
    public required string ModuleName { get; init; }

    /// <summary>The version of the module that produced <see cref="AuditEvent"/>.</summary>
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    /// <summary>The audit record itself, as the sink received it.</summary>
    [JsonPropertyName("auditEvent")]
    public required AuditEvent AuditEvent { get; init; }

    private static readonly JsonSerializerOptions SerializerOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Serializes this payload to the bytes handed to the encryption port.</summary>
    public byte[] ToUtf8Bytes() => JsonSerializer.SerializeToUtf8Bytes(this, SerializerOptions);
}
