using BenchmarkDotNet.Attributes;

namespace Lukdrasil.StepUpLogging.Benchmarks;

public class RequestPathRedactionBenchmarks
{
    private const string Path = "/api/orders/8f3a2c1e/items/42/token/xyz9";

    private CompiledRedactionPatterns _patterns = null!;
    private Dictionary<string, object?> _routeValues = null!;

    [GlobalSetup]
    public void Setup()
    {
        _patterns = new CompiledRedactionPatterns(
        [
            StepUpLoggingExtensions.CompilePattern(@"token/[^/]+"),
            StepUpLoggingExtensions.CompilePattern(@"password=[^&]+"),
            StepUpLoggingExtensions.CompilePattern(@"api[_-]?key=[^&]+"),
            StepUpLoggingExtensions.CompilePattern(@"Bearer\s+[A-Za-z0-9._-]+"),
            StepUpLoggingExtensions.CompilePattern(@"\b\d{16}\b"),
        ]);
        _routeValues = new Dictionary<string, object?>
        {
            ["orderId"] = "8f3a2c1e",
            ["itemId"] = "42",
            ["token"] = "xyz9",
        };
    }

    [Benchmark]
    public Dictionary<string, object?> RedactPathAndRouteValues() =>
        RequestPathRedaction.RedactRouteValues(_routeValues, Path, _patterns.Redact(Path), _patterns);
}
