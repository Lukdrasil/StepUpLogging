using System.Text.Json;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// Reads the top-level <c>code</c> of an <c>application/problem+json</c> rejection body (issue #71).
/// Anything that is not a small problem document with a string <c>code</c> is "no code", never a
/// failure: the code only informs an <see cref="IAuditRejectionObserver"/>, and the record is
/// dead-lettered either way.
/// </summary>
internal static class RejectionProblemCode
{
    internal const string ProblemJsonMediaType = "application/problem+json";

    /// <summary>
    /// The largest body read for a code. A problem document is a few hundred bytes; a larger body is
    /// not one worth parsing on the drain path.
    /// </summary>
    internal const int MaxBodyBytes = 16 * 1024;

    private static ReadOnlySpan<byte> CodeProperty => "code"u8;

    /// <summary>
    /// The code of <paramref name="content"/>, or <see langword="null"/> when its media type is not
    /// <see cref="ProblemJsonMediaType"/> (compared case-insensitively, parameters ignored).
    /// </summary>
    internal static async Task<string?> ReadAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (!string.Equals(content.Headers.ContentType?.MediaType, ProblemJsonMediaType, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var body = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return Parse(body);
    }

    /// <summary>
    /// The top-level string <c>code</c> of <paramref name="body"/>, or <see langword="null"/> when the
    /// body is over <see cref="MaxBodyBytes"/>, malformed, not an object, or has no string code there
    /// that decodes to text.
    /// </summary>
    internal static string? Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxBodyBytes)
        {
            return null;
        }

        try
        {
            return TopLevelCode(body);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // InvalidOperationException: a string code that does not decode (a lone surrogate
            // escape, invalid UTF-8) makes GetString throw instead of the reader.
            return null;
        }
    }

    private static string? TopLevelCode(ReadOnlySpan<byte> body)
    {
        var reader = new Utf8JsonReader(body);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return null;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isCode = reader.ValueTextEquals(CodeProperty);
            reader.Read();
            if (isCode)
            {
                return StringValue(ref reader);
            }

            reader.Skip();
        }

        return null;
    }

    private static string? StringValue(ref Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
}
