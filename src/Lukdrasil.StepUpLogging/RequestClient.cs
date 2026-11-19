using Microsoft.AspNetCore.Http;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// The one rule for who sent a request (ADR 0008), shared by request logging, the request summary and the audit
/// trail: the client address and the User-Agent.
/// </summary>
internal static class RequestClient
{
    /// <summary>
    /// The client's address and the redacted raw <c>X-Forwarded-For</c> header of a request.
    /// </summary>
    /// <remarks>
    /// SECURITY: <c>X-Forwarded-For</c> is client-supplied and trivially spoofable. By default the returned
    /// <c>ClientIp</c> is <c>Connection.RemoteIpAddress</c>, which the client cannot forge; the raw header is
    /// returned separately as <c>ForwardedFor</c> (redacted, since it is client-supplied). Only when
    /// <paramref name="trustForwardedHeaders"/> is <c>true</c>, which you should enable solely behind a
    /// reverse proxy you control with <c>ForwardedHeadersMiddleware</c> configured, is the first XFF entry
    /// used as <c>ClientIp</c>.
    /// </remarks>
    internal static (string? ClientIp, string? ForwardedFor) Addresses(
        HttpContext httpContext, bool trustForwardedHeaders, CompiledRedactionPatterns patterns)
    {
        try
        {
            var (forwardedFor, firstEntry) = RedactedForwardedFor(httpContext.Request.Headers, patterns);
            var clientIp = trustForwardedHeaders && firstEntry is not null
                ? firstEntry
                : httpContext.Connection.RemoteIpAddress?.ToString();

            return (clientIp, forwardedFor);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>The raw User-Agent header, or <see langword="null"/> when it is absent or blank.</summary>
    internal static string? UserAgent(HttpRequest request)
    {
        try
        {
            var values = request.Headers.UserAgent;
            var userAgent = values.Count > 0 ? values[0] : null;
            return string.IsNullOrWhiteSpace(userAgent) ? null : userAgent;
        }
        catch
        {
            return null;
        }
    }

    // The redacted raw X-Forwarded-For header and its redacted first entry; null when absent or blank.
    private static (string? ForwardedFor, string? FirstEntry) RedactedForwardedFor(IHeaderDictionary headers, CompiledRedactionPatterns patterns)
    {
        var rawValue = headers.TryGetValue("X-Forwarded-For", out var header) && header.Count > 0 ? header[0] : null;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return (null, null);
        }

        var forwardedFor = patterns.Redact(rawValue);
        var comma = rawValue.IndexOf(',');
        var candidate = (comma < 0 ? rawValue : rawValue[..comma]).Trim();
        return (forwardedFor, string.IsNullOrWhiteSpace(candidate) ? null : patterns.Redact(candidate));
    }
}
