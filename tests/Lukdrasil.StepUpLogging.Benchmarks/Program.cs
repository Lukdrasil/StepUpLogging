using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Lukdrasil.StepUpLogging.Benchmarks;

[assembly: Config(typeof(NuGetDependencyConfig))]

namespace Lukdrasil.StepUpLogging.Benchmarks;

internal static class Program
{
    private static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

/// <summary>
/// Lets the benchmarks reference <c>Serilog.Enrichers.OpenTelemetry</c> directly (<c>DroppedPathBenchmarks</c>
/// builds its ladder from the same enrichers the library runs): that NuGet package ships a non-optimized
/// build, which BenchmarkDotNet's validator rejects for any assembly that names it. The library project is
/// still built in Release.
/// </summary>
internal sealed class NuGetDependencyConfig : ManualConfig
{
    public NuGetDependencyConfig() => WithOptions(ConfigOptions.DisableOptimizationsValidator);
}
