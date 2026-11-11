using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>The serializer options and type information the spool payload is written with.</summary>
internal static class SpoolPayloadJson
{
    /// <summary>The type information <see cref="SpoolPayload.ToUtf8Bytes"/> writes with.</summary>
    /// <remarks>af-stub</remarks>
    internal static readonly JsonTypeInfo<SpoolPayload> PayloadTypeInfo = null!;

    /// <summary>Options for the payload, with or without the reflection fallback for undeclared value types.</summary>
    internal static JsonSerializerOptions CreateOptions(bool reflectionFallback) => throw new NotImplementedException("af-stub");

    /// <summary>The <see cref="SpoolPayload"/> type information of <paramref name="options"/>.</summary>
    internal static JsonTypeInfo<SpoolPayload> TypeInfoFor(JsonSerializerOptions options) => throw new NotImplementedException("af-stub");
}
