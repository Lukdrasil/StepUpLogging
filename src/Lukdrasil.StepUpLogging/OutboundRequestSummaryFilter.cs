using System.Collections.Frozen;
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;
using Microsoft.Extensions.Http;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Puts an <see cref="OutboundRequestSummaryHandler"/> outermost in the handler chain of every
/// <c>IHttpClientFactory</c> client except <see cref="ExcludedClients"/> (ADR 0032 D4), so a retried call is
/// summarized once. Adds nothing unless <see cref="StepUpLoggingOptions.AlwaysLogOutboundRequestSummary"/> is true.
/// </summary>
internal sealed class OutboundRequestSummaryFilter(StepUpLoggingController controller, CompiledRedactionPatterns patterns, StepUpSettings settings) : IHttpMessageHandlerBuilderFilter
{
    private readonly RequestSummaryWriter _summaries = new(settings, patterns, controller);

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

    /// <inheritdoc />
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => settings.AlwaysLogOutboundRequestSummary
        ? builder =>
        {
            next(builder);
            if (!IsExcluded(builder.Name))
            {
                builder.AdditionalHandlers.Insert(0, new OutboundRequestSummaryHandler(builder.Name ?? string.Empty, _summaries));
            }
        }
        : next;

    /// <summary>Whether the client named <paramref name="name"/> gets no outbound summary.</summary>
    internal static bool IsExcluded(string? name) => name is not null && ExcludedClients.Contains(name);
}
