using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>Source-generated metadata for <see cref="SpoolEnvelope"/>, with no naming policy (ADR 0035 D2).</summary>
[JsonSerializable(typeof(SpoolEnvelope))]
internal sealed partial class SpoolEnvelopeJsonContext : JsonSerializerContext;

/// <summary>
/// Source-generated metadata for <see cref="SpoolPayload"/> in camelCase, plus the value types an
/// audit record's <c>Data</c>, <c>OldValues</c> and <c>NewValues</c> may hold (ADR 0035 D2, D3).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(SpoolPayload))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(byte))]
[JsonSerializable(typeof(short))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(DateOnly))]
[JsonSerializable(typeof(TimeOnly))]
[JsonSerializable(typeof(TimeSpan))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(object?[]))]
[JsonSerializable(typeof(List<object?>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(int[]))]
internal sealed partial class SpoolPayloadJsonContext : JsonSerializerContext;

/// <summary>The serializer options and type information the spool payload is written with.</summary>
internal static class SpoolPayloadJson
{
    /// <summary>The type information <see cref="SpoolPayload.ToUtf8Bytes"/> writes with.</summary>
    internal static readonly JsonTypeInfo<SpoolPayload> PayloadTypeInfo = TypeInfoFor(CreateOptions(reflectionFallback: true));

    /// <summary>Options for the payload, with or without the reflection fallback for undeclared value types.</summary>
    internal static JsonSerializerOptions CreateOptions(bool reflectionFallback) =>
        new(SpoolPayloadJsonContext.Default.Options)
        {
            TypeInfoResolver = reflectionFallback && JsonSerializer.IsReflectionEnabledByDefault
                ? JsonTypeInfoResolver.Combine(SpoolPayloadJsonContext.Default, ReflectionFallback())
                : SpoolPayloadJsonContext.Default,
        };

    /// <summary>The <see cref="SpoolPayload"/> type information of <paramref name="options"/>.</summary>
    internal static JsonTypeInfo<SpoolPayload> TypeInfoFor(JsonSerializerOptions options) =>
        (JsonTypeInfo<SpoolPayload>)options.GetTypeInfo(typeof(SpoolPayload));

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Reached only while JsonSerializer.IsReflectionEnabledByDefault is true.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Reached only while JsonSerializer.IsReflectionEnabledByDefault is true.")]
    private static IJsonTypeInfoResolver ReflectionFallback() => new DefaultJsonTypeInfoResolver();
}
