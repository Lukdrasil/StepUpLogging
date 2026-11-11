using System.Text;
using System.Text.Json;
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// The spool's source-generated JSON: the envelope and the payload keep the bytes the reflection
/// serializer wrote, a declared <c>Data</c> value type needs no reflection, and an undeclared one
/// only works while the reflection fallback is on (ADR 0035).
/// </summary>
public class SpoolJsonContextTests
{
    private static readonly JsonSerializerOptions ReflectionOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record OrderDto(string Sku, int Quantity);

    private static SpoolPayload PayloadWith(object? dataValue) => new()
    {
        ModuleName = GoldenSpoolPayload.ModuleName,
        Version = GoldenSpoolPayload.Version,
        AuditEvent = GoldenSpoolPayload.CreateAuditEvent() with { Data = new Dictionary<string, object?> { ["v"] = dataValue } }
    };

    private static object? DataValue(string kind) => kind switch
    {
        "long" => 9_007_199_254_740_993L,
        "double" => 0.1d,
        "decimal" => 12.50m,
        "bool" => true,
        "Guid" => new Guid("0198f0c1-1111-7222-8333-444455556666"),
        "DateTimeOffset" => new DateTimeOffset(2026, 8, 13, 10, 11, 12, TimeSpan.FromHours(2)),
        "string[]" => new[] { "open", "closed" },
        "nested dictionary" => new Dictionary<string, object?> { ["status"] = "open", ["attempt"] = 2 },
        "JsonElement" => JsonElement.Parse("""{"sku":"A-1","quantity":3}"""),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static string ContextOnly(SpoolPayload payload) =>
        Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(payload, SpoolPayloadJson.TypeInfoFor(SpoolPayloadJson.CreateOptions(reflectionFallback: false))));

    private static string Reflection(SpoolPayload payload) =>
        Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(payload, ReflectionOptions));

    [Fact]
    public void SpoolEnvelope_ToUtf8Bytes_IsTheGoldenJson()
    {
        var bytes = GoldenSpoolEnvelope.Create().ToUtf8Bytes();

        Assert.Equal(GoldenSpoolEnvelope.Json, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void SpoolEnvelope_FromUtf8Bytes_OfTheGoldenJson_HasTheGoldenFields()
    {
        var expected = GoldenSpoolEnvelope.Create();

        var envelope = SpoolEnvelope.FromUtf8Bytes(Encoding.UTF8.GetBytes(GoldenSpoolEnvelope.Json));

        Assert.NotNull(envelope);
        Assert.Equal(expected.EventId, envelope.EventId);
        Assert.Equal(expected.CreatedUtc, envelope.CreatedUtc);
        Assert.Equal(expected.Payload, envelope.Payload);
    }

    [Fact]
    public void SpoolPayload_WithoutReflectionFallback_WritesTheGoldenPayloadJson()
    {
        var payload = new SpoolPayload
        {
            ModuleName = GoldenSpoolPayload.ModuleName,
            Version = GoldenSpoolPayload.Version,
            AuditEvent = GoldenSpoolPayload.CreateAuditEvent()
        };

        Assert.Equal(GoldenSpoolPayload.Json, ContextOnly(payload));
    }

    [Theory]
    [InlineData("long")]
    [InlineData("double")]
    [InlineData("decimal")]
    [InlineData("bool")]
    [InlineData("Guid")]
    [InlineData("DateTimeOffset")]
    [InlineData("string[]")]
    [InlineData("nested dictionary")]
    [InlineData("JsonElement")]
    public void SpoolPayload_DeclaredDataValueType_WithoutReflection_MatchesReflectionBytes(string kind)
    {
        var payload = PayloadWith(DataValue(kind));

        Assert.Equal(Reflection(payload), ContextOnly(payload));
    }

    [Fact]
    public void SpoolPayload_UndeclaredDataValueType_WithoutReflection_ThrowsNotSupported()
    {
        var payload = PayloadWith(new OrderDto("A-1", 3));

        Assert.Throws<NotSupportedException>(() => ContextOnly(payload));
    }

    [Fact]
    public void SpoolPayload_UndeclaredDataValueType_WithReflectionFallback_SerializesAsBefore()
    {
        var payload = PayloadWith(new OrderDto("A-1", 3));

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, SpoolPayloadJson.PayloadTypeInfo);

        Assert.Equal(Reflection(payload), Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void SpoolReader_EnvelopeMissingARequiredMember_IsCorrupt()
    {
        using var spool = new TempSpoolDirectory();
        File.WriteAllText(
            Path.Combine(spool.FullPath, GoldenSpoolEnvelope.FileName),
            """{"eventId":"0198f0c1-1111-7222-8333-444455556666","createdUtc":"2026-08-13T10:11:12.1234567+00:00"}""");

        var entry = Assert.Single(new SpoolReader(spool.FullPath).ReadOldestFirst());

        Assert.True(entry.IsCorrupt);
    }
}
