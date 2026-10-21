using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Writes one outbound request summary per call through an <c>IHttpClientFactory</c> client (ADR 0032 D4), after the
/// call ends or fails. A failed call is rethrown unchanged; writing the summary never fails the call.
/// </summary>
internal sealed class OutboundRequestSummaryHandler(string clientName, StepUpLoggingController controller, CompiledRedactionPatterns patterns, RequestSummaryDetail detail) : DelegatingHandler
{
    /// <summary>The <c>HttpClientName</c> of the unnamed default client.</summary>
    internal const string DefaultClientName = "(default)";

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => ObserveAsync(request, () => base.SendAsync(request, cancellationToken));

    /// <inheritdoc />
    /// <remarks>The inner call completes before <see cref="ObserveAsync"/> awaits it, so this never blocks on a task.</remarks>
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        => ObserveAsync(request, () => Task.FromResult(base.Send(request, cancellationToken))).GetAwaiter().GetResult();

    private async Task<HttpResponseMessage> ObserveAsync(HttpRequestMessage request, Func<Task<HttpResponseMessage>> send)
    {
        var start = Stopwatch.GetTimestamp();
        HttpResponseMessage? response = null;
        Exception? failure = null;
        try
        {
            response = await send().ConfigureAwait(false);
            return response;
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            Write(request, response, failure, start);
        }
    }

    /// <summary>The outbound summary of one call; <paramref name="response"/> is null when it failed.</summary>
    internal static RequestSummary Summarize(string clientName, HttpRequestMessage request, HttpResponseMessage? response, Exception? failure, double elapsedMs, RequestSummaryDetail detail, CompiledRedactionPatterns patterns)
    {
        var summary = new RequestSummary(
                RequestSummary.Outbound, request.Method.Method, patterns.Redact(PathOf(request.RequestUri)), (int?)response?.StatusCode, elapsedMs,
                request.Headers.UserAgent.ToString(), patterns)
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

    private void Write(HttpRequestMessage request, HttpResponseMessage? response, Exception? failure, long start)
    {
        try
        {
            controller.EmitRequestSummary(Summarize(clientName, request, response, failure, Stopwatch.GetElapsedTime(start).TotalMilliseconds, detail, patterns));
        }
        catch
        {
            // Never let the summary fail or mask the call.
        }
    }

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
