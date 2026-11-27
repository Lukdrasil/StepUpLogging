using Microsoft.AspNetCore.Http;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Builds the outbound request summary of one <c>IHttpClientFactory</c> call (ADR 0032 D3, D4): the Compact fields
/// always, the Full fields on top when the detail is <see cref="RequestSummaryDetail.Full"/>.
/// </summary>
internal static class OutboundRequestSummary
{
    /// <summary>The <c>HttpClientName</c> of the unnamed default client.</summary>
    internal const string DefaultClientName = "(default)";

    /// <summary>The outbound summary of one call; <paramref name="response"/> is null when it failed.</summary>
    internal static RequestSummary From(string clientName, HttpRequestMessage request, HttpResponseMessage? response, Exception? failure, double elapsedMs, RequestSummaryDetail detail, CompiledRedactionPatterns patterns)
    {
        var summary = new RequestSummary(
                RequestSummary.Outbound, request.Method.Method, patterns.Redact(PathOf(request.RequestUri)), (int?)response?.StatusCode, elapsedMs,
                request.Headers.UserAgent.ToString(), patterns, detail == RequestSummaryDetail.Minimal)
            .With("HttpClientName", clientName.Length == 0 ? DefaultClientName : clientName)
            .With("Host", HostOf(request.RequestUri, patterns));
        if (detail == RequestSummaryDetail.Full)
        {
            AddFull(summary, request, response, failure);
        }

        return summary;
    }

    /// <summary>The path of an absolute URI without its query; empty for a missing or relative one.</summary>
    internal static string PathOf(Uri? uri) => uri is { IsAbsoluteUri: true } ? uri.AbsolutePath : string.Empty;

    private static void AddFull(RequestSummary summary, HttpRequestMessage request, HttpResponseMessage? response, Exception? failure)
        => summary
            .With("Protocol", ProtocolOf(response))
            .With("RequestContentLength", request.Content?.Headers.ContentLength)
            .With("ResponseContentLength", response?.Content.Headers.ContentLength)
            .With("ExceptionType", failure?.GetType().FullName);

    private static string? ProtocolOf(HttpResponseMessage? response)
        => response is null ? null : ProtocolName(response.Version);

    /// <summary>The protocol name of <paramref name="version"/>; <c>HTTP/{version}</c> for one ASP.NET Core does not name.</summary>
    private static string ProtocolName(Version version)
    {
        try
        {
            return HttpProtocol.GetHttpProtocol(version);
        }
        catch (ArgumentOutOfRangeException)
        {
            return $"HTTP/{version}";
        }
    }

    private static string? HostOf(Uri? uri, CompiledRedactionPatterns patterns)
        => uri is { IsAbsoluteUri: true } ? patterns.Redact(uri.Authority) : null;
}
