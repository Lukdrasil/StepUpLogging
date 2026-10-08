using BenchmarkDotNet.Attributes;

namespace Lukdrasil.StepUpLogging.Benchmarks;

/// <summary>
/// What <see cref="CompiledRedactionPatterns.Redact"/> costs on the three inputs that matter: a short
/// value that matches nothing, a long header value that matches nothing, and a value that matches.
/// The pattern count shows how the cost grows with the patterns an application configures.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Logging")]
public class RedactionPatternBenchmarks
{
    private const string ShortValue = "/api/orders/42";
    private const string SecretValue = "/api/orders/8f3a2c1e/token/xyz9";

    private CompiledRedactionPatterns _patterns = null!;
    private string _longHeader = null!;

    /// <summary>How many of the five sample patterns are configured.</summary>
    [Params(1, 5)]
    public int PatternCount { get; set; }

    /// <summary>Compiles the patterns and checks the three inputs behave as their rows say.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _patterns = BenchmarkFixtures.SamplePatterns(PatternCount);
        _longHeader = string.Join("; ", Enumerable.Range(0, 24).Select(i => $"segment{i}=an ordinary header value"));

        BenchmarkFixtures.Require(ReferenceEquals(RedactShortNoMatch(), ShortValue), "the short value was changed");
        BenchmarkFixtures.Require(ReferenceEquals(RedactLongHeaderNoMatch(), _longHeader), "the long header was changed");
        BenchmarkFixtures.Require(RedactMatch() != SecretValue, "the secret was not masked");
    }

    /// <summary>A short value no pattern matches.</summary>
    [Benchmark]
    public string RedactShortNoMatch() => _patterns.Redact(ShortValue);

    /// <summary>A header value of about a thousand characters no pattern matches.</summary>
    [Benchmark]
    public string RedactLongHeaderNoMatch() => _patterns.Redact(_longHeader);

    /// <summary>A path the first pattern matches.</summary>
    [Benchmark]
    public string RedactMatch() => _patterns.Redact(SecretValue);
}
