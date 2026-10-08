using System.Text.RegularExpressions;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// The compiled redaction patterns and the redaction they apply (ADR 0001, ADR 0022). When every
/// pattern is linear-time and shares its options and timeout, one union of them is tested first: an
/// input the union cannot match is returned as is, which is what the per-pattern loop would return.
/// </summary>
internal sealed class CompiledRedactionPatterns
{
    /// <summary>The sentinel returned in place of a value whose redaction failed, so a secret is never leaked.</summary>
    internal const string RedactionError = "[REDACTION-ERROR]";

    private const string Replacement = "[REDACTED]";
    private const char CommentMarker = '#';

    private readonly Regex? _union;

    public CompiledRedactionPatterns(Regex[] patterns)
    {
        Patterns = patterns;
        _union = BuildUnion(patterns);
    }

    internal Regex[] Patterns { get; }

    /// <summary>Whether a union prefilter was built for the patterns.</summary>
    internal bool HasPrefilter => _union is not null;

    public string Redact(string input)
    {
        if (string.IsNullOrEmpty(input) || Patterns.Length == 0 || CannotMatch(input)) return input;
        return RedactEach(input);
    }

    private bool CannotMatch(string input)
    {
        if (_union is null) return false;
        try
        {
            return !_union.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private string RedactEach(string input)
    {
        foreach (var pattern in Patterns)
        {
            try
            {
                input = pattern.Replace(input, Replacement);
            }
            catch
            {
                // Fail closed: a pattern that throws (e.g. RegexMatchTimeoutException) must never
                // leak the original value. Continue with the remaining patterns on the sentinel.
                input = RedactionError;
            }
        }
        return input;
    }

    private static Regex? BuildUnion(Regex[] patterns)
    {
        if (!CanUnion(patterns)) return null;
        var source = string.Join('|', patterns.Select(p => $"(?:{p})"));
        try
        {
            return new Regex(source, patterns[0].Options, patterns[0].MatchTimeout);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The union equals the patterns taken one by one only for linear-time patterns that share their options
    /// and timeout; a '#' is excluded because an inline <c>(?x)</c> comment would swallow the branches after it.
    /// </summary>
    private static bool CanUnion(Regex[] patterns)
        => patterns.Length >= 2
           && patterns[0].Options.HasFlag(RegexOptions.NonBacktracking)
           && patterns.All(p => p.Options == patterns[0].Options
                                && p.MatchTimeout == patterns[0].MatchTimeout
                                && !p.ToString().Contains(CommentMarker));
}
