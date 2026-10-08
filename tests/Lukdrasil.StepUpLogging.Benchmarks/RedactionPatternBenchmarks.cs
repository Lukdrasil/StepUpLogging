using System.Text.RegularExpressions;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace Lukdrasil.StepUpLogging.Benchmarks;

/// <summary>
/// What <see cref="CompiledRedactionPatterns.Redact"/> costs on the three inputs that matter: a short
/// value that matches nothing, a long header value that matches nothing, and a value that matches.
/// The pattern count shows how the cost grows with the patterns an application configures. Each input
/// is a group whose baseline is the sequential per-pattern loop <c>Redact</c> replaced, so the ratio is
/// what the union prefilter saves.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Logging")]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class RedactionPatternBenchmarks
{
    private const string ShortValue = "/api/orders/42";
    private const string SecretValue = "/api/orders/8f3a2c1e/token/xyz9";
    private const string ShortNoMatchCategory = "ShortNoMatch";
    private const string LongHeaderNoMatchCategory = "LongHeaderNoMatch";
    private const string MatchCategory = "Match";

    private CompiledRedactionPatterns _patterns = null!;

    /// <summary>How many of the five sample patterns are configured.</summary>
    [Params(1, 5)]
    public int PatternCount { get; set; }

    /// <summary>Compiles the patterns and checks the three inputs behave as their rows say.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _patterns = BenchmarkFixtures.SamplePatterns(PatternCount);

        BenchmarkFixtures.Require(ReferenceEquals(RedactShortNoMatch(), ShortValue), "the short value was changed");
        BenchmarkFixtures.Require(ReferenceEquals(RedactLongHeaderNoMatch(), BenchmarkFixtures.LongHeader), "the long header was changed");
        BenchmarkFixtures.Require(RedactMatch() != SecretValue, "the secret was not masked");
        BenchmarkFixtures.Require(LoopMatch() == RedactMatch(), "the loop and Redact disagree");
    }

    /// <summary>The loop over a short value no pattern matches.</summary>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory(ShortNoMatchCategory)]
    public string LoopShortNoMatch() => SequentialRedact(ShortValue);

    /// <summary>A short value no pattern matches.</summary>
    [Benchmark]
    [BenchmarkCategory(ShortNoMatchCategory)]
    public string RedactShortNoMatch() => _patterns.Redact(ShortValue);

    /// <summary>The loop over a header value of about a thousand characters no pattern matches.</summary>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory(LongHeaderNoMatchCategory)]
    public string LoopLongHeaderNoMatch() => SequentialRedact(BenchmarkFixtures.LongHeader);

    /// <summary>A header value of about a thousand characters no pattern matches.</summary>
    [Benchmark]
    [BenchmarkCategory(LongHeaderNoMatchCategory)]
    public string RedactLongHeaderNoMatch() => _patterns.Redact(BenchmarkFixtures.LongHeader);

    /// <summary>The loop over a path the first pattern matches.</summary>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory(MatchCategory)]
    public string LoopMatch() => SequentialRedact(SecretValue);

    /// <summary>A path the first pattern matches.</summary>
    [Benchmark]
    [BenchmarkCategory(MatchCategory)]
    public string RedactMatch() => _patterns.Redact(SecretValue);

    /// <summary>The redaction before the prefilter: every pattern in order, each failing closed on its own.</summary>
    private string SequentialRedact(string input)
    {
        foreach (var pattern in _patterns.Patterns)
        {
            input = ReplaceOrSentinel(pattern, input);
        }

        return input;
    }

    private static string ReplaceOrSentinel(Regex pattern, string input)
    {
        try
        {
            return pattern.Replace(input, "[REDACTED]");
        }
        catch (RegexMatchTimeoutException)
        {
            return CompiledRedactionPatterns.RedactionError;
        }
    }
}
