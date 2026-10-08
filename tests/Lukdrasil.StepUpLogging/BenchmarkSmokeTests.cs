using System.Reflection;
using BenchmarkDotNet.Attributes;
using Lukdrasil.StepUpLogging.Benchmarks;

namespace Lukdrasil.StepUpLogging.Tests;

public class BenchmarkSmokeTests
{
    private const string BenchmarkNamespace = "Lukdrasil.StepUpLogging.Benchmarks.";
    private const string DiskCategory = "Disk";

    private static readonly string[] SuiteCategories = ["Logging", "Audit", DiskCategory];

    private static readonly Assembly BenchmarkAssembly = typeof(DurableWriteBenchmarks).Assembly;

    /// <summary>The benchmark classes cheap enough to run once in <c>dotnet test</c>; every other one is marked Disk.</summary>
    private static readonly string[] SmokeRunClassNames =
    [
        "StepUpSinkBenchmarks",
        "PreErrorBufferSinkBenchmarks",
        "EnricherBenchmarks",
        "RedactionPatternBenchmarks",
        "PipelineBenchmarks",
        "RequestLoggingBenchmarks",
        "AuditBenchmarks",
        "RequestPathRedactionBenchmarks",
        "DroppedPathBenchmarks",
    ];

    public static TheoryData<string> SmokeRunClasses => new(SmokeRunClassNames);

    [Theory]
    [MemberData(nameof(SmokeRunClasses))]
    public async Task Benchmark_RunsOnceWithItsFirstParameters(string benchmarkClass)
    {
        var type = BenchmarkAssembly.GetType(BenchmarkNamespace + benchmarkClass);

        Assert.NotNull(type);
        await BenchmarkSmokeRunner.RunOnceAsync(type);
    }

    [Fact]
    public void EveryBenchmarkClass_IsSmokeRunOrMarkedDisk()
    {
        var neither = BenchmarkClasses()
            .Where(type => !SmokeRunClassNames.Contains(type.Name) && !CategoriesOf(type).Contains(DiskCategory))
            .Select(type => type.Name);

        Assert.Empty(neither);
    }

    [Fact]
    public void EveryBenchmarkClass_DeclaresASuiteCategory()
    {
        var withoutExactlyOne = BenchmarkClasses()
            .Where(type => CategoriesOf(type).Count(SuiteCategories.Contains) != 1)
            .Select(type => type.Name);

        Assert.Empty(withoutExactlyOne);
    }

    private static IEnumerable<Type> BenchmarkClasses() =>
        BenchmarkAssembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false }
                && type.GetMethods().Any(method => method.IsDefined(typeof(BenchmarkAttribute))));

    private static string[] CategoriesOf(Type type) =>
        type.GetCustomAttributes<BenchmarkCategoryAttribute>(inherit: true).SelectMany(category => category.Categories).Distinct().ToArray();
}
