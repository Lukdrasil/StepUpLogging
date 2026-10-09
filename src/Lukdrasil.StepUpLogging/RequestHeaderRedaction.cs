using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Lukdrasil.StepUpLogging;

internal static class RequestHeaderRedaction
{
    private const string Redacted = "[REDACTED]";

    /// <summary>
    /// Joins the non-null values of a header with <c>", "</c>. A single value is returned as it is, without
    /// allocating.
    /// </summary>
    internal static string JoinHeaderValues(StringValues values) => values.Count switch
    {
        0 => string.Empty,
        1 => values[0] ?? string.Empty,
        _ => string.Join(", ", values.Where(v => v is not null))
    };

    /// <summary>Redacts every request header, noting each redacted one as <c>header:{Key}</c>.</summary>
    internal static Dictionary<string, object?> RedactRequestHeaders(
        IHeaderDictionary headers,
        HashSet<string> sensitiveHeaders,
        CompiledRedactionPatterns patterns,
        Action<string> noteRedaction)
    {
        var result = new Dictionary<string, object?>(headers.Count);
        foreach (var header in headers)
        {
            if (sensitiveHeaders.Contains(header.Key))
            {
                result[header.Key] = Redacted;
                noteRedaction($"header:{header.Key}");
                continue;
            }

            var value = JoinHeaderValues(header.Value);
            var redactedValue = patterns.Redact(value);
            if (!string.Equals(redactedValue, value, StringComparison.Ordinal))
            {
                noteRedaction($"header:{header.Key}");
            }
            result[header.Key] = redactedValue;
        }
        return result;
    }
}
