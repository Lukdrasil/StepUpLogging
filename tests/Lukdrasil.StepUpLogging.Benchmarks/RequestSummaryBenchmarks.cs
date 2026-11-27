using BenchmarkDotNet.Attributes;
using Serilog;

namespace Lukdrasil.StepUpLogging.Benchmarks;

/// <summary>
/// What <see cref="RequestSummaryWriter.Write"/> costs to build and write one request
/// summary to a logger that only counts, with the four required values alone and with the optional
/// fields: query string, route parameters, user agent, client address, token identifier and
/// forwarded-for header.
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

    private static readonly CompiledRedactionPatterns NoPatterns = new([]);

    private readonly NullLogEventSink _summaries = new();
    private Serilog.Core.Logger _logger = null!;
    private RequestSummaryWriter _writer = null!;

    /// <summary>Whether the summary carries every optional field, or only the method, path, status code and elapsed time.</summary>
    [Params(false, true)]
    public bool AllFields { get; set; }

    /// <summary>Builds the writer over a counting logger and checks that one call writes one summary.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_summaries).CreateLogger();
        _writer = new RequestSummaryWriter(new StepUpSettings(new StepUpLoggingOptions()), NoPatterns, () => _logger);

        var written = _summaries.Received;
        EmitRequestSummary();
        BenchmarkFixtures.Require(_summaries.Received - written == 1, "one call did not write exactly one request summary");
    }

    /// <summary>Disposes the logger under the writer.</summary>
    [GlobalCleanup]
    public void Cleanup() => _logger.Dispose();

    /// <summary>One request summary.</summary>
    [Benchmark]
    public void EmitRequestSummary() =>
        _writer.Write(
            new RequestSummary(
                    RequestSummary.Inbound,
                    Method,
                    BenchmarkFixtures.RequestPath,
                    StatusCode,
                    ElapsedMs,
                    AllFields ? "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36" : null,
                    NoPatterns)
                .With("ClientIp", AllFields ? "203.0.113.7" : null)
                .With(LogProperties.Jti, AllFields ? "4d2f0c1e-6a52-4b6a-9c3b-0f6c2d9a77e1" : null)
                .With("QueryString", AllFields ? "?page=2&sort=asc" : null)
                .With("RouteParameters", AllFields ? RouteParameters : null)
                .With("ForwardedFor", AllFields ? "203.0.113.7, 10.0.0.1" : null));
}
