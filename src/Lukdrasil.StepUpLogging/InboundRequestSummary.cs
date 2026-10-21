using System.Security.Claims;
using System.Security.Principal;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Builds the inbound request summary (ADR 0032 D3): the Compact fields always, the Full fields on top when the
/// detail is <see cref="RequestSummaryDetail.Full"/>. Every client-supplied value is redacted (ADR 0032 D5).
/// </summary>
internal static class InboundRequestSummary
{
    // Zero patterns: Redact returns its input, for values the caller has already redacted.
    private static readonly CompiledRedactionPatterns AlreadyRedacted = new([]);

    /// <summary>The summary of the request in <paramref name="context"/>.</summary>
    internal static RequestSummary From(HttpContext context, string path, int statusCode, double elapsedMs, RequestSummaryDetail detail, bool trustForwardedHeaders, CompiledRedactionPatterns patterns)
    {
        var redactedPath = patterns.Redact(path);
        var (clientIp, forwardedFor) = StepUpLoggingExtensions.ExtractClientAddresses(context, trustForwardedHeaders, patterns);
        var summary = new RequestSummary(RequestSummary.Inbound, context.Request.Method, redactedPath, statusCode, elapsedMs, StepUpLoggingExtensions.ExtractUserAgent(context.Request), patterns, detail == RequestSummaryDetail.Minimal)
            .With("Route", RouteOf(context))
            .WithTrue("Aborted", context.RequestAborted.IsCancellationRequested)
            .With("ClientId", Redacted(patterns, "ClientId", ClientIdOf(context.User)));
        AddSharedCompact(summary, clientIp, Redacted(patterns, LogProperties.Jti, JtiOf(context.User)));
        if (detail == RequestSummaryDetail.Full)
        {
            AddFull(summary, context, path, redactedPath, forwardedFor, patterns);
        }

        return summary;
    }

    /// <summary>
    /// The summary of the public <c>EmitRequestSummary</c> arguments, which the caller has already redacted.
    /// </summary>
    internal static RequestSummary FromArguments(string method, string path, int statusCode, double elapsedMs, string? queryString, IReadOnlyDictionary<string, object?>? routeParameters, string? userAgent, string? clientIp, string? jti, string? forwardedFor, RequestSummaryDetail detail)
    {
        var summary = new RequestSummary(RequestSummary.Inbound, method, path, statusCode, elapsedMs, userAgent, AlreadyRedacted, detail == RequestSummaryDetail.Minimal);
        AddSharedCompact(summary, clientIp, jti);
        if (detail == RequestSummaryDetail.Full)
        {
            AddSharedFull(summary, queryString, routeParameters, forwardedFor);
        }

        return summary;
    }

    /// <summary>The <c>azp</c> claim, else the <c>client_id</c> claim, else <see langword="null"/>.</summary>
    internal static string? ClientIdOf(ClaimsPrincipal? user)
        => user?.FindFirst("azp")?.Value ?? user?.FindFirst("client_id")?.Value;

    private static void AddSharedCompact(RequestSummary summary, string? clientIp, string? jti)
        => summary.With("ClientIp", clientIp).With(LogProperties.Jti, jti);

    private static void AddSharedFull(RequestSummary summary, string? queryString, IReadOnlyDictionary<string, object?>? routeParameters, string? forwardedFor)
        => summary.With("QueryString", queryString).With("RouteParameters", routeParameters).With("ForwardedFor", forwardedFor);

    private static void AddFull(RequestSummary summary, HttpContext context, string path, string redactedPath, string? forwardedFor, CompiledRedactionPatterns patterns)
    {
        var request = context.Request;
        summary
            .With("Protocol", request.Protocol)
            .With("Scheme", request.Scheme)
            .With("Host", patterns.Redact(request.Host.Value ?? string.Empty))
            .With("RequestContentLength", request.ContentLength)
            .With("ResponseContentLength", context.Response.ContentLength);
        AddIdentity(summary, context.User?.Identity);
        summary.With("EndpointName", context.GetEndpoint()?.DisplayName);
        AddSharedFull(summary, patterns.Redact(request.QueryString.Value ?? string.Empty), RouteParametersOf(context, path, redactedPath, patterns), forwardedFor);
    }

    private static void AddIdentity(RequestSummary summary, IIdentity? identity)
        => summary.With("IsAuthenticated", identity?.IsAuthenticated ?? false).With("AuthScheme", identity?.AuthenticationType);

    private static string? RouteOf(HttpContext context) => (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;

    private static string? JtiOf(ClaimsPrincipal? user) => user?.FindFirst("jti")?.Value;

    private static string? Redacted(CompiledRedactionPatterns patterns, string name, string? value)
        => value is null ? null : patterns.RedactProperty(name, value);

    private static IReadOnlyDictionary<string, object?>? RouteParametersOf(HttpContext context, string path, string redactedPath, CompiledRedactionPatterns patterns)
        => context.Request.RouteValues?.Count > 0
            ? RequestPathRedaction.RedactRouteValues(context.Request.RouteValues, path, redactedPath, patterns)
            : null;
}
