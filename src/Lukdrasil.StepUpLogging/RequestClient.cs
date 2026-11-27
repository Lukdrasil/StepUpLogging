using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// The one rule for who sent a request (ADR 0008), shared by request logging, the request summary and the audit
/// trail: the client address and the User-Agent.
/// </summary>
internal static class RequestClient
{
    /// <summary>
    /// The client's address and the redacted raw <c>X-Forwarded-For</c> header of a request, written as
    /// <paramref name="mode"/> says (ADR 0042): both omitted under <see cref="ClientIpMode.Off"/>, each address masked
    /// to its client prefix under <see cref="ClientIpMode.Prefix"/>, unchanged under <see cref="ClientIpMode.Full"/>.
    /// </summary>
    /// <remarks>
    /// SECURITY: <c>X-Forwarded-For</c> is client-supplied and trivially spoofable. By default the returned
    /// <c>ClientIp</c> is <c>Connection.RemoteIpAddress</c>, which the client cannot forge; the raw header is
    /// returned separately as <c>ForwardedFor</c> (redacted, since it is client-supplied). Only when
    /// <paramref name="trustForwardedHeaders"/> is <c>true</c>, which you should enable solely behind a
    /// reverse proxy you control with <c>ForwardedHeadersMiddleware</c> configured, is the first XFF entry
    /// used as <c>ClientIp</c>. Under <see cref="ClientIpMode.Prefix"/> a forwarded value is parsed after redaction,
    /// so a value that does not parse, or that a pattern changed, is omitted rather than written raw.
    /// </remarks>
    internal static (string? ClientIp, string? ForwardedFor) Addresses(
        HttpContext httpContext, bool trustForwardedHeaders, ClientIpMode mode, CompiledRedactionPatterns patterns)
    {
        if (mode == ClientIpMode.Off)
        {
            return (null, null);
        }

        try
        {
            var (forwardedFor, firstEntry) = RedactedForwardedFor(httpContext.Request.Headers, patterns);
            var remoteIp = httpContext.Connection.RemoteIpAddress;
            if (mode == ClientIpMode.Full)
            {
                return (trustForwardedHeaders && firstEntry is not null ? firstEntry : remoteIp?.ToString(), forwardedFor);
            }

            var clientIp = trustForwardedHeaders && firstEntry is not null ? PrefixOfEntry(firstEntry) : PrefixOf(remoteIp);
            return (clientIp, PrefixOfEachEntry(forwardedFor));
        }
        catch
        {
            return (null, null);
        }
    }

    // The client prefix of each entry that parses, joined with ", "; null when none does.
    private static string? PrefixOfEachEntry(string? forwardedFor)
    {
        if (forwardedFor is null)
        {
            return null;
        }

        var prefixes = forwardedFor.Split(',', StringSplitOptions.TrimEntries).Select(PrefixOfEntry).OfType<string>().ToArray();
        return prefixes.Length == 0 ? null : string.Join(", ", prefixes);
    }

    // An entry may carry a port or brackets ("198.51.100.9:5678", "[2001:db8::1]:443").
    private static string? PrefixOfEntry(string entry)
        => IPEndPoint.TryParse(entry, out var endpoint) ? PrefixOf(endpoint.Address) : null;

    // The /24 network of an IPv4 address (also of an IPv4-mapped IPv6 one), the /48 network of an IPv6 address.
    private static string? PrefixOf(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        var prefixBytes = address.AddressFamily == AddressFamily.InterNetwork ? 3 : 6;
        Array.Clear(bytes, prefixBytes, bytes.Length - prefixBytes);
        return $"{new IPAddress(bytes)}/{prefixBytes * 8}";
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
