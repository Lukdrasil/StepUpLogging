using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Serilog;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Tests;

public class InboundRequestSummaryTests
{
    private const string Path = "/orders/42";
    private const string RoutePattern = "/orders/{id}";

    private static readonly CompiledRedactionPatterns NoPatterns = new([]);

    private static readonly Serilog.ILogger Logger = new LoggerConfiguration().CreateLogger();

    private static readonly string[] CompactKeys =
        ["Direction", "Method", "Path", "StatusCode", "ElapsedMs", "ClientKind", "UserAgent", "Route", "Aborted", "ClientId", "ClientIp", "Jti", "IsRequestSummary"];

    private static readonly string[] FullOnlyKeys =
        ["Protocol", "Scheme", "Host", "RequestContentLength", "ResponseContentLength", "IsAuthenticated", "AuthScheme", "EndpointName", "QueryString", "RouteParameters", "ForwardedFor"];

    private static readonly string[] EarlierKeys =
        ["Method", "Path", "StatusCode", "ElapsedMs", "ForwardedFor", "Jti", "ClientIp", "UserAgent", "RouteParameters", "QueryString", "IsRequestSummary"];

    private static DefaultHttpContext Context(
        string path = Path,
        string userAgent = "curl/8.5.0",
        string host = "api.example",
        string query = "?page=2",
        string forwardedFor = "203.0.113.9",
        bool routed = true,
        bool aborted = true,
        params Claim[] claims)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Scheme = "https";
        context.Request.Protocol = "HTTP/1.1";
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Request.ContentLength = 3;
        context.Request.Headers.UserAgent = userAgent;
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.7");
        context.Response.ContentLength = 5;
        if (routed)
        {
            context.Request.RouteValues["id"] = path[(path.LastIndexOf('/') + 1)..];
            context.SetEndpoint(new RouteEndpoint(
                _ => Task.CompletedTask, RoutePatternFactory.Parse(RoutePattern), 0, EndpointMetadataCollection.Empty, "Orders by id"));
        }

        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            claims.Length > 0 ? claims : [new Claim("azp", "billing-ui"), new Claim("jti", "jti-1")], "Bearer"));
        if (aborted)
        {
            var abort = new CancellationTokenSource();
            abort.Cancel();
            context.RequestAborted = abort.Token;
        }

        return context;
    }

    private static LogEvent From(HttpContext context, RequestSummaryDetail detail = RequestSummaryDetail.Full, CompiledRedactionPatterns? patterns = null, string path = Path)
        => InboundRequestSummary.From(context, path, 200, 1.5, detail, false, patterns ?? NoPatterns)
            .ToLogEvent(LogEventLevel.Information, Logger);

    private static LogEvent FromArguments(RequestSummaryDetail detail)
        => InboundRequestSummary.FromArguments(
                "GET", Path, 200, 1.5, "?page=2", new Dictionary<string, object?> { ["id"] = "42" }, "curl/8.5.0",
                "198.51.100.7", "jti-1", "203.0.113.9", detail)
            .ToLogEvent(LogEventLevel.Information, Logger);

    private static object? Scalar(LogEvent logEvent, string name) => ((ScalarValue)logEvent.Properties[name]).Value;

    [Fact]
    public void Compact_WritesExactlyTheCompactFields()
    {
        var logEvent = From(Context(), RequestSummaryDetail.Compact);

        Assert.Equal(CompactKeys, logEvent.Properties.Keys);
        Assert.Equal("Inbound", Scalar(logEvent, "Direction"));
    }

    [Fact]
    public void Full_WritesEveryField()
    {
        var logEvent = From(Context(), RequestSummaryDetail.Full);

        Assert.Equal(CompactKeys.Concat(FullOnlyKeys).Order(), logEvent.Properties.Keys.Order());
        Assert.Equal("HTTP/1.1", Scalar(logEvent, "Protocol"));
        Assert.Equal("https", Scalar(logEvent, "Scheme"));
        Assert.Equal("api.example", Scalar(logEvent, "Host"));
        Assert.Equal(3L, Scalar(logEvent, "RequestContentLength"));
        Assert.Equal(5L, Scalar(logEvent, "ResponseContentLength"));
        Assert.Equal(true, Scalar(logEvent, "IsAuthenticated"));
        Assert.Equal("Bearer", Scalar(logEvent, "AuthScheme"));
        Assert.Equal("Orders by id", Scalar(logEvent, "EndpointName"));
    }

    [Fact]
    public void DefaultOptions_AreFull_AndKeepEveryEarlierProperty()
    {
        var detail = new StepUpLoggingOptions().RequestSummaryDetail;

        var logEvent = From(Context(), detail);

        Assert.Equal(RequestSummaryDetail.Full, detail);
        Assert.All(EarlierKeys, key => Assert.Contains(key, logEvent.Properties.Keys));
    }

    [Fact]
    public void RoutedEndpoint_WritesRoute()
    {
        var logEvent = From(Context());

        Assert.Equal(RoutePattern, Scalar(logEvent, "Route"));
    }

    [Fact]
    public void NoEndpoint_LeavesRouteOut()
    {
        var logEvent = From(Context(routed: false));

        Assert.False(logEvent.Properties.ContainsKey("Route"));
    }

    [Fact]
    public void AzpClaim_WritesClientId()
    {
        var logEvent = From(Context(claims: [new Claim("azp", "billing-ui"), new Claim("client_id", "other-client")]));

        Assert.Equal("billing-ui", Scalar(logEvent, "ClientId"));
    }

    [Fact]
    public void ClientIdClaim_IsTheFallback()
    {
        var logEvent = From(Context(claims: [new Claim("client_id", "billing-ui")]));

        Assert.Equal("billing-ui", Scalar(logEvent, "ClientId"));
    }

    [Fact]
    public void NoClientClaim_LeavesClientIdOut()
    {
        var logEvent = From(Context(claims: [new Claim("jti", "jti-1")]));

        Assert.False(logEvent.Properties.ContainsKey("ClientId"));
    }

    [Fact]
    public void AbortedRequest_WritesAbortedTrue()
    {
        var logEvent = From(Context(aborted: true));

        Assert.Equal(true, Scalar(logEvent, "Aborted"));
    }

    [Fact]
    public void CompletedRequest_LeavesAbortedOut()
    {
        var logEvent = From(Context(aborted: false));

        Assert.False(logEvent.Properties.ContainsKey("Aborted"));
    }

    [Fact]
    public void MatchingPattern_RedactsInboundFields()
    {
        var patterns = new CompiledRedactionPatterns([new Regex("secret-[A-Za-z0-9]+")]);
        var context = Context(
            path: "/orders/secret-p1",
            userAgent: "agent/1 secret-ua",
            host: "secret-host.example",
            query: "?token=secret-q",
            forwardedFor: "secret-xff",
            claims: [new Claim("azp", "secret-azp"), new Claim("jti", "secret-jti")]);

        var logEvent = From(context, RequestSummaryDetail.Full, patterns, "/orders/secret-p1");

        Assert.All(
            new[] { "Path", "UserAgent", "ClientId", "Jti", "QueryString", "ForwardedFor", "Host" },
            name => Assert.Contains("[REDACTED]", (string)Scalar(logEvent, name)!, StringComparison.Ordinal));
        Assert.Contains("[REDACTED]", logEvent.Properties["RouteParameters"].ToString(), StringComparison.Ordinal);
        Assert.All(logEvent.Properties, p => Assert.DoesNotContain("secret-", p.Value.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void FromArguments_Compact_WritesOnlyClientIpAndJtiOfTheOptionals()
    {
        var logEvent = FromArguments(RequestSummaryDetail.Compact);

        Assert.Equal(
            ["Direction", "Method", "Path", "StatusCode", "ElapsedMs", "ClientKind", "UserAgent", "ClientIp", "Jti", "IsRequestSummary"],
            logEvent.Properties.Keys);
    }

    [Fact]
    public void FromAndFromArguments_Full_ShareTheTrailingFieldOrder()
    {
        string[] trailing = ["QueryString", "RouteParameters", "ForwardedFor", "IsRequestSummary"];

        foreach (var logEvent in new[] { From(Context(), RequestSummaryDetail.Full), FromArguments(RequestSummaryDetail.Full) })
        {
            var keys = logEvent.Properties.Keys.ToList();
            Assert.Equal(trailing, keys.TakeLast(trailing.Length));
            Assert.True(keys.IndexOf("ClientIp") < keys.IndexOf("Jti"), string.Join(", ", keys));
        }
    }
}
