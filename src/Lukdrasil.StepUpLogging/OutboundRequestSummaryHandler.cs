using System.Diagnostics;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Writes one outbound request summary per call through an <c>IHttpClientFactory</c> client (ADR 0032 D4), after the
/// call ends or fails. A failed call is rethrown unchanged; writing the summary never fails the call.
/// </summary>
internal sealed class OutboundRequestSummaryHandler(string clientName, RequestSummaryWriter summaries) : DelegatingHandler
{
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
            summaries.WriteOutbound(clientName, request, response, failure, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }
}
