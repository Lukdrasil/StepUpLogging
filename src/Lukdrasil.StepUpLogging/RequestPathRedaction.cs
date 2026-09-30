namespace Lukdrasil.StepUpLogging;

internal static class RequestPathRedaction
{
    private const string Redacted = "[REDACTED]";

    public static Dictionary<string, object?> RedactRouteValues(
        IEnumerable<KeyValuePair<string, object?>> routeValues,
        string rawPath,
        string redactedPath,
        CompiledRedactionPatterns patterns,
        Action<string>? onRedacted = null)
    {
        var result = new Dictionary<string, object?>();
        foreach (var (key, rawValue) in routeValues)
        {
            var value = rawValue?.ToString() ?? string.Empty;
            var redactedValue = CountOccurrences(redactedPath, value) < CountOccurrences(rawPath, value)
                ? Redacted
                : patterns.Redact(value);
            if (!string.Equals(redactedValue, value, StringComparison.Ordinal))
            {
                onRedacted?.Invoke($"route:{key}");
            }
            result[key] = redactedValue;
        }
        return result;
    }

    private static int CountOccurrences(string text, string value)
    {
        if (value.Length == 0) return 0;
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
