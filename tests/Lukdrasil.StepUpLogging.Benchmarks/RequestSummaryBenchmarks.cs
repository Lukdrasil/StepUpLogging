using BenchmarkDotNet.Attributes;
using Serilog;

namespace Lukdrasil.StepUpLogging.Benchmarks;

/// <summary>
/// What <see cref="StepUpLoggingController.EmitRequestSummary"/> costs to build and write one request
/// summary to a logger that only counts, with the four required values alone and with every optional
/// field the middleware passes: query string, route parameters, user agent, client address, token
/// identifier and forwarded-for header.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Logging")]
public class RequestSummaryBenchmarks
{
    private const string Method = "GET";
    private const int StatusCode = 200;
    private const double ElapsedMs = 12.5;

    private static readonly IReadOnlyDictionary<string, object?> RouteParameters = new Dictionary<string, object?>
    {
        ["orderId"] = "8f3a2c1e",
        ["itemId"] = "42",
        ["token"] = "[REDACTED]",
    };

    private readonly NullLogEventSink _summaries = new();
    private StepUpLoggingController _controller = null!;

    /// <summary>Whether the summary carries every optional field, or only the method, path, status code and elapsed time.</summary>
    [Params(false, true)]
    public bool AllFields { get; set; }

    /// <summary>Builds the controller over a counting logger and checks that one call writes one summary.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_summaries).CreateLogger();
        _controller = new StepUpLoggingController(new StepUpLoggingOptions(), logger);

        var written = _summaries.Received;
        EmitRequestSummary();
        BenchmarkFixtures.Require(_summaries.Received - written == 1, "one call did not write exactly one request summary");
    }

    /// <summary>Disposes the controller, which disposes the logger under it.</summary>
    [GlobalCleanup]
    public void Cleanup() => _controller.Dispose();

    /// <summary>One request summary.</summary>
    [Benchmark]
    public void EmitRequestSummary() =>
        _controller.EmitRequestSummary(
            Method,
            BenchmarkFixtures.RequestPath,
            StatusCode,
            ElapsedMs,
            queryString: AllFields ? "?page=2&sort=asc" : null,
            routeParameters: AllFields ? RouteParameters : null,
            userAgent: AllFields ? "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36" : null,
            clientIp: AllFields ? "203.0.113.7" : null,
            jti: AllFields ? "4d2f0c1e-6a52-4b6a-9c3b-0f6c2d9a77e1" : null,
            forwardedFor: AllFields ? "203.0.113.7, 10.0.0.1" : null);
}
