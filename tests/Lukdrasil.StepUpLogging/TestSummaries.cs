namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>Inbound request summaries built from already redacted field values, for tests of what the controller writes.</summary>
internal static class TestSummaries
{
    private static readonly CompiledRedactionPatterns NoPatterns = new([]);

    internal static RequestSummary Inbound(
        string method,
        string path,
        int statusCode,
        double elapsedMs,
        string? queryString = null,
        IReadOnlyDictionary<string, object?>? routeParameters = null,
        string? userAgent = null,
        string? clientIp = null,
        string? jti = null,
        string? forwardedFor = null)
        => new RequestSummary(RequestSummary.Inbound, method, path, statusCode, elapsedMs, userAgent, NoPatterns)
            .With("ClientIp", clientIp)
            .With(LogProperties.Jti, jti)
            .With("QueryString", queryString)
            .With("RouteParameters", routeParameters)
            .With("ForwardedFor", forwardedFor);
}
