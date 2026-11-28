using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// The one rule for who sent a request (ADR 0008), shared by request logging, the request summary and the audit
/// trail: the client address, the User-Agent and the ids of the caller's token.
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
            var trustedEntry = trustForwardedHeaders ? firstEntry : null;
            var remoteIp = httpContext.Connection.RemoteIpAddress;
            if (mode == ClientIpMode.Full)
            {
                return (trustedEntry ?? remoteIp?.ToString(), forwardedFor);
            }

            var clientIp = trustedEntry is not null ? PrefixOfEntry(trustedEntry) : PrefixOf(remoteIp);
            return (clientIp, PrefixOfEachEntry(forwardedFor));
        }
        catch
        {
            return (null, null);
        }
    }

    private static string? PrefixOfEachEntry(string? forwardedFor)
    {
        if (forwardedFor is null)
        {
            return null;
        }

        var prefixes = forwardedFor.Split(',', StringSplitOptions.TrimEntries).Select(PrefixOfEntry).OfType<string>().ToArray();
        return prefixes.Length == 0 ? null : string.Join(", ", prefixes);
    }

    // An entry may carry a port or brackets ("198.51.100.9:5678", "[2001:db8::1]:443"). An IPv4 entry must be the
    // dotted quad itself: the parser also reads shorthand such as "42" or "0x7f.0.0.1" as an address.
    private static string? PrefixOfEntry(string entry)
    {
        if (!IPEndPoint.TryParse(entry, out var endpoint))
        {
            return null;
        }

        var address = endpoint.Address;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var text = address.ToString();
            if (entry != text && !entry.StartsWith(text + ":", StringComparison.Ordinal))
            {
                return null;
            }
        }

        return PrefixOf(address);
    }

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

    /// <summary>
    /// The connection's address and source port, written only under <see cref="ClientIpMode.Full"/> (ADR 0044 D3).
    /// The address is returned only when <paramref name="trustForwardedHeaders"/> is <c>true</c>: otherwise
    /// <c>ClientIp</c> already is the connection address. The port is omitted when the server does not know it.
    /// </summary>
    internal static (string? RemoteIp, int? RemotePort) Remote(HttpContext httpContext, bool trustForwardedHeaders, ClientIpMode mode)
    {
        var connection = httpContext.Connection;
        if (mode != ClientIpMode.Full)
        {
            return (null, null);
        }

        return (trustForwardedHeaders ? connection.RemoteIpAddress?.ToString() : null, connection.RemotePort > 0 ? connection.RemotePort : null);
    }

    /// <summary>
    /// The caller token's <c>jti</c> and its session id, the <c>sid</c> claim else the <see cref="ClaimTypes.Sid"/>
    /// claim the JWT handler maps it to (ADR 0045 D1), each through <see cref="CompiledRedactionPatterns.RedactProperty"/>
    /// and <see langword="null"/> when the claim is absent or empty.
    /// </summary>
    internal static (string? Jti, string? Sid) TokenIds(ClaimsPrincipal? user, CompiledRedactionPatterns patterns)
        => (Redacted(patterns, LogProperties.Jti, user?.FindFirst("jti")?.Value),
            Redacted(patterns, LogProperties.Sid, user?.FindFirst("sid")?.Value ?? user?.FindFirst(ClaimTypes.Sid)?.Value));

    private static string? Redacted(CompiledRedactionPatterns patterns, string name, string? value)
        => string.IsNullOrEmpty(value) ? null : patterns.RedactProperty(name, value);

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
