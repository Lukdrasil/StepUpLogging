using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// The one <c>SourceContext</c> prefix rule shared by every category option (ADR 0021 D3): a category
/// matches a prefix when it equals it ordinally, or begins with it immediately followed by a <c>.</c>.
/// </summary>
internal static class CategoryPrefix
{
    private const string SourceContextPropertyName = "SourceContext";

    public static bool Matches(string sourceContext, string prefix)
        => sourceContext.Equals(prefix, StringComparison.Ordinal)
           || (sourceContext.Length > prefix.Length
               && sourceContext[prefix.Length] == '.'
               && sourceContext.StartsWith(prefix, StringComparison.Ordinal));

    public static bool TryGetSourceContext(LogEvent logEvent, out string sourceContext)
    {
        if (logEvent.Properties.TryGetValue(SourceContextPropertyName, out var value)
            && value is ScalarValue { Value: string source })
        {
            sourceContext = source;
            return true;
        }

        sourceContext = string.Empty;
        return false;
    }

    public static bool MatchesAny(LogEvent logEvent, string[] prefixes)
        => prefixes.Length != 0 && TryGetSourceContext(logEvent, out var source) && MatchesAny(source, prefixes);

    public static bool MatchesAny(string source, string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (Matches(source, prefix)) return true;
        }
        return false;
    }
}
