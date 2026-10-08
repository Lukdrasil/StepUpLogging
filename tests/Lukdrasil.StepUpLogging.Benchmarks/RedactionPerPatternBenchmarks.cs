using BenchmarkDotNet.Attributes;

namespace Lukdrasil.StepUpLogging.Benchmarks;

/// <summary>
/// What one sample pattern costs alone on the two inputs it does not match: the per-pattern scan that the
/// union prefilter replaces with one scan for the whole set. <see cref="PatternIndex"/> selects the pattern
/// from <see cref="BenchmarkFixtures.PatternSources"/>; a single pattern builds no prefilter.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Logging")]
public class RedactionPerPatternBenchmarks
{
    private const string ShortValue = "/api/orders/42";

    private CompiledRedactionPatterns _pattern = null!;

    /// <summary>Which of the five sample patterns runs alone.</summary>
    [Params(0, 1, 2, 3, 4)]
    public int PatternIndex { get; set; }

    /// <summary>Compiles the selected pattern and checks it matches neither input.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _pattern = new CompiledRedactionPatterns([StepUpLoggingExtensions.CompilePattern(BenchmarkFixtures.PatternSources[PatternIndex])]);

        BenchmarkFixtures.Require(!_pattern.HasPrefilter, "a single pattern built a prefilter");
        BenchmarkFixtures.Require(ReferenceEquals(ShortNoMatch(), ShortValue), "the short value was changed");
        BenchmarkFixtures.Require(ReferenceEquals(LongHeaderNoMatch(), BenchmarkFixtures.LongHeader), "the long header was changed");
    }

    /// <summary>A short value the pattern does not match.</summary>
    [Benchmark]
    public string ShortNoMatch() => _pattern.Redact(ShortValue);

    /// <summary>A header value of about a thousand characters the pattern does not match.</summary>
    [Benchmark]
    public string LongHeaderNoMatch() => _pattern.Redact(BenchmarkFixtures.LongHeader);
}
