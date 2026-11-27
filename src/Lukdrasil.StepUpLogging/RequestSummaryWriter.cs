using Microsoft.AspNetCore.Http;
using Serilog;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Writes the inbound and outbound request summaries (ADR 0032): builds each at the configured
/// <c>RequestSummaryDetail</c> with the redaction patterns and writes it through the summary logger at
/// <c>RequestSummaryLevel</c>. A summary that fails to build or write is dropped, so it never fails or masks the
/// request or call it describes.
/// </summary>
/// <remarks>
/// <paramref name="summaryLogger"/> is read on every write. The production logger is the controller's bypass logger,
/// which the pipeline attaches after this module is built and which the controller owns and disposes (ADR 0003).
/// </remarks>
internal sealed class RequestSummaryWriter(StepUpSettings settings, CompiledRedactionPatterns patterns, Func<ILogger> summaryLogger)
{
    /// <summary>A writer over the bypass logger of <paramref name="controller"/>.</summary>
    internal RequestSummaryWriter(StepUpSettings settings, CompiledRedactionPatterns patterns, StepUpLoggingController controller)
        : this(settings, patterns, () => controller.SummaryLogger)
    {
    }

    /// <summary>Writes the summary of the request in <paramref name="context"/>, unless its path is excluded.</summary>
    internal void WriteInbound(HttpContext context, int statusCode, double elapsedMs)
    {
        try
        {
            var path = ExcludedPaths.NormalizedPath(context.Request);
            if (!settings.ExcludedPaths.Contains(path))
            {
                Emit(InboundRequestSummary.From(context, path, statusCode, elapsedMs, settings.RequestSummaryDetail, settings.TrustForwardedHeaders, patterns));
            }
        }
        catch
        {
            // Never let the summary fail or mask the request.
        }
    }

    /// <summary>Writes the summary of one call through the client named <paramref name="clientName"/>; <paramref name="response"/> is null when it failed.</summary>
    internal void WriteOutbound(string clientName, HttpRequestMessage request, HttpResponseMessage? response, Exception? failure, double elapsedMs)
    {
        try
        {
            Emit(OutboundRequestSummary.From(clientName, request, response, failure, elapsedMs, settings.RequestSummaryDetail, patterns));
        }
        catch
        {
            // Never let the summary fail or mask the call.
        }
    }

    /// <summary>Writes a summary already built.</summary>
    /// <remarks>
    /// Route values are converted per value with the logger's scalar conversion, so a <c>ToMaximumCollectionCount</c>
    /// or destructuring depth limit on a consumer-supplied summary logger is not applied to <c>RouteParameters</c>
    /// (ADR 0009). The library's own bypass logger has no such settings.
    /// </remarks>
    internal void Write(RequestSummary summary)
    {
        try
        {
            Emit(summary);
        }
        catch
        {
            // Never let the summary fail the caller.
        }
    }

    private void Emit(RequestSummary summary)
    {
        var logger = summaryLogger();
        if (logger.IsEnabled(settings.RequestSummaryLevel))
        {
            logger.Write(summary.ToLogEvent(settings.RequestSummaryLevel, logger));
        }
    }
}
