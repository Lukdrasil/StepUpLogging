using Microsoft.AspNetCore.Http;

namespace Lukdrasil.StepUpLogging;

// af-stub
internal static class InboundRequestSummary
{
    internal static RequestSummary From(HttpContext context, string path, int statusCode, double elapsedMs, RequestSummaryDetail detail, bool trustForwardedHeaders, CompiledRedactionPatterns patterns)
        => throw new NotImplementedException("af-stub");

    internal static RequestSummary FromArguments(string method, string path, int statusCode, double elapsedMs, string? queryString, IReadOnlyDictionary<string, object?>? routeParameters, string? userAgent, string? clientIp, string? jti, string? forwardedFor, RequestSummaryDetail detail)
        => throw new NotImplementedException("af-stub");
}
