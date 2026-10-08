using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Pins the union prefilter of <see cref="CompiledRedactionPatterns"/>: one test per build rule through
/// <c>HasPrefilter</c>, and <c>Redact</c> output byte-identical to the sequential per-pattern loop.
/// </summary>
public class RedactionShortcutPrefilterTests
{
    private const int RandomSeed = 20261008;
    private const int RandomInputCount = 500;
    private const int MaxFragmentsPerInput = 8;

    private static readonly string[] SamplePatternSources =
    [
        @"token/[^/]+",
        @"password=[^&]+",
        @"api[_-]?key=[^&]+",
        @"Bearer\s+[A-Za-z0-9._-]+",
        @"\b\d{16}\b",
    ];

    private static readonly string[] Fragments =
    [
        "token/", "TOKEN/", "abc", "/", "password=", "PassWord=", "&", "api_key=", "api-key=", "apikey=", "API_KEY=",
        "Bearer ", "bearer\t", "x.y-z_", "1234567890123456", "12345678901234567", "\u212A", "\u0130", "\u0131",
        "#", "[REDACTED]", " ", "\n", "=", "secret", "ap\u0130_key=", "to\u212Aen/",
    ];

    public static TheoryData<string> AdversarialInputs =>
    [
        "token/abc/password=x&api_key=y",
        "Bearer abc.def-ghi_jkl and Bearer   x",
        "card 1234567890123456 end",
        "card 12345678901234567 end",
        "TOKEN/UPPER/PASSWORD=Shout&API-KEY=k",
        "api\u212Aey=kelvin&to\u212Aen/kelvin",
        "\u0130 token/\u0131 password=\u0130&",
        "# token/#hash#/ #",
        "[REDACTED] token/[REDACTED]",
        "password=&apikey=&Bearer ",
        "x\nBearer y\ntoken/z\n",
        "nothing to hide here",
        "",
    ];

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 4)]
    public void HasPrefilter_TwoCompilePatternSamples_IsTrue(int first, int second)
    {
        var patterns = new CompiledRedactionPatterns([Sample(first), Sample(second)]);

        Assert.True(patterns.HasPrefilter);
    }

    [Fact]
    public void HasPrefilter_AllFiveSamples_IsTrue()
    {
        Assert.True(AllSamples().HasPrefilter);
    }

    [Fact]
    public void HasPrefilter_OnePattern_IsFalse()
    {
        var patterns = new CompiledRedactionPatterns([Sample(0)]);

        Assert.False(patterns.HasPrefilter);
    }

    [Fact]
    public void HasPrefilter_PatternWithHash_IsFalse()
    {
        var patterns = new CompiledRedactionPatterns(
            [StepUpLoggingExtensions.CompilePattern("(?x)a#c"), StepUpLoggingExtensions.CompilePattern("b")]);

        Assert.False(patterns.HasPrefilter);
    }

    [Fact]
    public void HasPrefilter_LookaheadFallbackWithSample_IsFalse()
    {
        var lookahead = StepUpLoggingExtensions.CompilePattern("secret(?=!)");
        Assert.False(lookahead.Options.HasFlag(RegexOptions.NonBacktracking));

        var patterns = new CompiledRedactionPatterns([lookahead, Sample(0)]);

        Assert.False(patterns.HasPrefilter);
    }

    [Fact]
    public void HasPrefilter_DifferentTimeouts_IsFalse()
    {
        var patterns = new CompiledRedactionPatterns(
        [
            new Regex("alpha", RegexOptions.NonBacktracking | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)),
            new Regex("beta", RegexOptions.NonBacktracking | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)),
        ]);

        Assert.False(patterns.HasPrefilter);
    }

    [Fact]
    public void HasPrefilter_TwoCompiledIgnoreCasePatterns_IsFalse()
    {
        var patterns = new CompiledRedactionPatterns(
        [
            new Regex("alpha", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)),
            new Regex("beta", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)),
        ]);

        Assert.False(patterns.HasPrefilter);
    }

    [Theory]
    [MemberData(nameof(AdversarialInputs))]
    public void Redact_MatchesSequentialOracle(string input)
    {
        var patterns = AllSamples();

        Assert.Equal(SequentialRedact(patterns.Patterns, input), patterns.Redact(input));
    }

    [Fact]
    public void Redact_SeededRandomInputs_MatchSequentialOracle()
    {
        var patterns = AllSamples();
        var random = new Random(RandomSeed);

        var mismatches = Enumerable.Range(0, RandomInputCount)
            .Select(_ => RandomInput(random))
            .Where(input => SequentialRedact(patterns.Patterns, input) != patterns.Redact(input))
            .ToList();

        Assert.Empty(mismatches);
    }

    [Fact]
    public void Redact_LaterPatternSeesEarlierRedaction()
    {
        var patterns = new CompiledRedactionPatterns(
            [StepUpLoggingExtensions.CompilePattern("secret"), StepUpLoggingExtensions.CompilePattern(@"x\[REDACTED\]")]);

        Assert.Equal("[REDACTED]", patterns.Redact("xsecret"));
    }

    [Fact]
    public void Redact_PrefilterNoMatch_ReturnsSameInstance()
    {
        var patterns = AllSamples();
        var input = new StringBuilder("nothing ").Append("to hide here").ToString();

        Assert.Same(input, patterns.Redact(input));
    }

    [Fact]
    public void Redact_MixedEngines_RedactsLikeLoop()
    {
        var patterns = new CompiledRedactionPatterns(
            [Sample(0), StepUpLoggingExtensions.CompilePattern("secret(?=!)")]);
        const string input = "token/abc/ secret! secret?";

        var redacted = patterns.Redact(input);

        Assert.Equal("[REDACTED]/ [REDACTED]! secret?", redacted);
        Assert.Equal(SequentialRedact(patterns.Patterns, input), redacted);
    }

    /// <summary>Compiles the sample pattern at <paramref name="index"/> the way the host compiles configured patterns.</summary>
    private static Regex Sample(int index) => StepUpLoggingExtensions.CompilePattern(SamplePatternSources[index]);

    /// <summary>All five sample patterns, compiled the way the host compiles them.</summary>
    private static CompiledRedactionPatterns AllSamples()
        => new(SamplePatternSources.Select(StepUpLoggingExtensions.CompilePattern).ToArray());

    /// <summary>Concatenates one to <see cref="MaxFragmentsPerInput"/> random <see cref="Fragments"/>.</summary>
    private static string RandomInput(Random random)
    {
        var builder = new StringBuilder();
        var count = random.Next(1, MaxFragmentsPerInput + 1);
        for (var i = 0; i < count; i++)
        {
            builder.Append(Fragments[random.Next(Fragments.Length)]);
        }

        return builder.ToString();
    }

    /// <summary>The reference redaction: every pattern in order, each failing closed to the sentinel on its own.</summary>
    private static string SequentialRedact(IReadOnlyList<Regex> patterns, string input)
    {
        if (string.IsNullOrEmpty(input) || patterns.Count == 0) return input;
        foreach (var pattern in patterns)
        {
            try
            {
                input = pattern.Replace(input, "[REDACTED]");
            }
            catch (RegexMatchTimeoutException)
            {
                input = CompiledRedactionPatterns.RedactionError;
            }
        }

        return input;
    }
}
