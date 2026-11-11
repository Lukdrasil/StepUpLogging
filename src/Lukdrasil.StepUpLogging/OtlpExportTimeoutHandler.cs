using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// The Serilog OTLP sink's HTTP handler: cancels one export after <paramref name="timeout"/>
/// (<c>OTEL_EXPORTER_OTLP_TIMEOUT</c>) instead of the 100 s <see cref="HttpClient"/> default, so disposing the
/// logger never waits long for a collector that does not answer (ADR 0036). The inner handler is the one the sink
/// uses by default.
/// </summary>
internal sealed class OtlpExportTimeoutHandler(TimeSpan timeout)
    : DelegatingHandler(new SocketsHttpHandler { ActivityHeadersPropagator = null })
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        return await base.SendAsync(request, cts.Token).ConfigureAwait(false);
    }
}
