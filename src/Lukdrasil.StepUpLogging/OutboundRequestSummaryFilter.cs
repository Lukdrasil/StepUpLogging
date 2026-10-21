using System.Collections.Frozen;
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Puts an <see cref="OutboundRequestSummaryHandler"/> outermost in the handler chain of every
/// <c>IHttpClientFactory</c> client except <see cref="ExcludedClients"/> (ADR 0032 D4), so a retried call is
/// summarized once. Registered only when <see cref="StepUpLoggingOptions.AlwaysLogOutboundRequestSummary"/> is true.
/// </summary>
internal sealed class OutboundRequestSummaryFilter(StepUpLoggingController controller, CompiledRedactionPatterns patterns, IOptions<StepUpLoggingOptions> options) : IHttpMessageHandlerBuilderFilter
{
    /// <summary>
    /// The library's own audit client and the OTLP exporter clients. The OTLP names are the ones
    /// OpenTelemetry.Exporter.OpenTelemetryProtocol 1.15.3 creates in TryEnableIHttpClientFactoryIntegration; the log
    /// exporter name is in that DLL although its README names only trace and metric. A test pins them
    /// (OutboundRequestSummaryRegistrationTests.OtlpExporter_FactoryClientNames_AreAllExcluded).
    /// </summary>
    internal static readonly FrozenSet<string> ExcludedClients = new[]
    {
        DrainWorker.HttpClientName,
        "OtlpTraceExporter",
        "OtlpMetricExporter",
        "OtlpLogExporter",
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly RequestSummaryDetail _detail = options.Value.RequestSummaryDetail;

    /// <inheritdoc />
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);
        if (!IsExcluded(builder.Name))
        {
            builder.AdditionalHandlers.Insert(0, new OutboundRequestSummaryHandler(builder.Name ?? string.Empty, controller, patterns, _detail));
        }
    };

    /// <summary>Whether the client named <paramref name="name"/> gets no outbound summary.</summary>
    internal static bool IsExcluded(string? name) => name is not null && ExcludedClients.Contains(name);
}
