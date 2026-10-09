using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Lukdrasil.StepUpLogging;

// af-stub: reproduces today's header loop (StepUpLoggingExtensions.cs) until the green step.
internal static class RequestHeaderRedaction
{
    private const string Redacted = "[REDACTED]";

    /// <summary>Joins the non-null values of a header with <c>", "</c>.</summary>
    internal static string JoinHeaderValues(StringValues values) =>
        string.Join(", ", values.Where(v => v != null) ?? Array.Empty<string>()); // af-stub

    /// <summary>Redacts every request header, noting each redacted one as <c>header:{Key}</c>.</summary>
    internal static Dictionary<string, object?> RedactRequestHeaders(
        IHeaderDictionary headers,
        HashSet<string> sensitiveHeaders,
        CompiledRedactionPatterns patterns,
        Action<string> noteRedaction)
    {
        var result = new Dictionary<string, object?>(); // af-stub
        foreach (var header in headers)
        {
            if (sensitiveHeaders.Contains(header.Key))
            {
                result[header.Key] = Redacted;
                noteRedaction($"header:{header.Key}");
            }
            else
            {
                var value = JoinHeaderValues(header.Value);
                var redactedValue = patterns.Redact(value);
                if (!string.Equals(redactedValue, value, StringComparison.Ordinal))
                {
                    noteRedaction($"header:{header.Key}");
                }
                result[header.Key] = redactedValue;
            }
        }
        return result;
    }
}
