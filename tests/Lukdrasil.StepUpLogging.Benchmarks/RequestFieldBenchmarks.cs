using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Http;

namespace Lukdrasil.StepUpLogging.Benchmarks;

/// <summary>
/// What the per-request field helpers of <c>UseStepUpRequestLogging()</c> cost on their own: the header
/// redaction, the client address and user agent extraction the request summary reads, and the excluded-path
/// check every request runs. <see cref="RequestLoggingBenchmarks"/> measures them only inside a whole request.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Logging")]
public class RequestFieldBenchmarks
{
    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Cookie", "X-API-Key", "X-Auth-Token", "X-Access-Token",
        "Authorization-Token", "Proxy-Authorization", "WWW-Authenticate", "Sec-WebSocket-Key"
    };

    private static readonly HashSet<string> ExcludedPaths = new(["/healthz", "/metrics", "/health"], StringComparer.OrdinalIgnoreCase);
    private static readonly string[] ExcludedPrefixes = ["/internal"];

    private CompiledRedactionPatterns _patterns = null!;
    private DefaultHttpContext _context = null!;
    private Action<string> _noteRedaction = null!;
    private int _notes;

    /// <summary>How many request headers the request carries on top of the user agent, forwarded-for and authorization headers.</summary>
    [Params(4, 32)]
    public int HeaderCount { get; set; }

    /// <summary>Builds the patterns and a request with a sensitive header, and checks each helper does its work.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _patterns = BenchmarkFixtures.SamplePatterns();
        _noteRedaction = _ => _notes++;
        _context = BenchmarkFixtures.Request(HeaderCount);
        _context.Request.Headers.Authorization = "Bearer abc.def.ghi";

        BenchmarkFixtures.Require(RedactHeaders()["Authorization"] is "[REDACTED]", "the authorization header was not redacted");
        BenchmarkFixtures.Require(ClientAddresses() == ("127.0.0.1", "203.0.113.7, 10.0.0.1"), "the client addresses were not extracted");
        BenchmarkFixtures.Require(UserAgent() is not null, "the user agent was not extracted");
        BenchmarkFixtures.Require(!ExcludedPath(), "the request path was excluded");
    }

    /// <summary>Redacts every request header, noting each redaction the way the middleware does.</summary>
    [Benchmark]
    public Dictionary<string, object?> RedactHeaders() =>
        RequestHeaderRedaction.RedactRequestHeaders(_context.Request.Headers, SensitiveHeaders, _patterns, _noteRedaction);

    /// <summary>Extracts and redacts the client address and the forwarded-for header.</summary>
    [Benchmark]
    public (string? ClientIp, string? ForwardedFor) ClientAddresses() =>
        StepUpLoggingExtensions.ExtractClientAddresses(_context, trustForwardedHeaders: false, _patterns);

    /// <summary>Extracts the user agent.</summary>
    [Benchmark]
    public string? UserAgent() => StepUpLoggingExtensions.ExtractUserAgent(_context.Request);

    /// <summary>Checks the request path against the default excluded paths and one wildcard prefix.</summary>
    [Benchmark]
    public bool ExcludedPath() => StepUpLoggingExtensions.IsExcludedPath(BenchmarkFixtures.RequestPath, ExcludedPaths, ExcludedPrefixes);
}
