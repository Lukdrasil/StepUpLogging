using System.Net;
using Microsoft.AspNetCore.Http;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Tests;

public class RequestSummaryWriterTests
{
    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public LogEvent? LastEvent => Events.LastOrDefault();

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private sealed class ThrowingLogger : Serilog.ILogger
    {
        public bool IsEnabled(LogEventLevel level) => true;

        public void Write(LogEvent logEvent) => throw new InvalidOperationException("sink failed");
    }

    private static Logger LoggerOver(CaptureSink sink) => new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

    private static HttpContext Request(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = path;
        return context;
    }

    private static object? Scalar(LogEvent logEvent, string name) => ((ScalarValue)logEvent.Properties[name]).Value;

    [Fact]
    public void Write_WritesSummaryToSummaryLogger()
    {
        var sink = new CaptureSink();
        using var logger = LoggerOver(sink);
        var routeParams = new Dictionary<string, object?> { ["id"] = "1", ["order"] = "abc" };

        TestSummaries.Writer(logger).Write(TestSummaries.Inbound("GET", "/api/values", 200, 123.45, queryString: "id=1&order=abc", routeParameters: routeParams));

        var summary = Assert.Single(sink.Events);
        Assert.Equal("{Direction} {Method} {Path} {StatusCode} {ElapsedMs} ms {ClientKind} {UserAgent}", summary.MessageTemplate.Text);
        Assert.True(summary.Properties.ContainsKey("IsRequestSummary"));
        Assert.Equal("GET", Scalar(summary, "Method"));
        Assert.Equal("/api/values", Scalar(summary, "Path"));
        Assert.Equal(200, Convert.ToInt32(Scalar(summary, "StatusCode")));
        Assert.Equal("id=1&order=abc", Scalar(summary, "QueryString"));
        Assert.True(summary.Properties.ContainsKey("RouteParameters"));
    }

    [Fact]
    public void Write_WithUserAgentAndClientIp_AddsProperties()
    {
        var sink = new CaptureSink();
        using var logger = LoggerOver(sink);

        TestSummaries.Writer(logger).Write(TestSummaries.Inbound("GET", "/api/test", 200, 50.0, userAgent: "Mozilla/5.0", clientIp: "127.0.0.1"));

        Assert.Equal("Mozilla/5.0", Scalar(sink.LastEvent!, "UserAgent"));
        Assert.Equal("127.0.0.1", Scalar(sink.LastEvent!, "ClientIp"));
    }

    [Fact]
    public void Write_WithNullUserAgentAndClientIp_WritesNoneAndOmitsClientIp()
    {
        var sink = new CaptureSink();
        using var logger = LoggerOver(sink);

        TestSummaries.Writer(logger).Write(TestSummaries.Inbound("GET", "/api/test", 200, 50.0, userAgent: null, clientIp: null));

        Assert.Equal("(none)", Scalar(sink.LastEvent!, "UserAgent"));
        Assert.Equal("None", Scalar(sink.LastEvent!, "ClientKind"));
        Assert.False(sink.LastEvent!.Properties.ContainsKey("ClientIp"));
    }

    [Fact]
    public void Write_WritesAtTheRequestSummaryLevel()
    {
        var sink = new CaptureSink();
        using var logger = LoggerOver(sink);

        TestSummaries.Writer(logger, new StepUpLoggingOptions { RequestSummaryLevel = "Debug" })
            .Write(TestSummaries.Inbound("GET", "/api/test", 200, 1.0));

        Assert.Equal(LogEventLevel.Debug, Assert.Single(sink.Events).Level);
    }

    [Fact]
    public void Write_ControllerLoggerAttachedAfterConstruction_WritesToTheAttachedLogger()
    {
        var sink = new CaptureSink();
        var options = new StepUpLoggingOptions();
        using var controller = new StepUpLoggingController(options, null);
        var writer = new RequestSummaryWriter(new StepUpSettings(options), new CompiledRedactionPatterns([]), controller);

        controller.SetSummaryLogger(LoggerOver(sink));
        writer.Write(TestSummaries.Inbound("GET", "/api/test", 200, 1.0));

        Assert.Single(sink.Events);
    }

    [Fact]
    public void Write_LoggerThrows_DoesNotThrow()
    {
        var writer = TestSummaries.Writer(new ThrowingLogger());

        var ex = Record.Exception(() => writer.Write(TestSummaries.Inbound("GET", "/api/test", 200, 1.0)));

        Assert.Null(ex);
    }

    [Fact]
    public void WriteInbound_WritesTheInboundSummaryOfTheRequest()
    {
        var sink = new CaptureSink();
        using var logger = LoggerOver(sink);

        TestSummaries.Writer(logger).WriteInbound(Request("/api/orders/"), 503, 2.5);

        var summary = Assert.Single(sink.Events);
        Assert.Equal("Inbound", Scalar(summary, "Direction"));
        Assert.Equal("/api/orders", Scalar(summary, "Path"));
        Assert.Equal(503, Scalar(summary, "StatusCode"));
    }

    [Fact]
    public void WriteInbound_ExcludedPath_WritesNothing()
    {
        var sink = new CaptureSink();
        using var logger = LoggerOver(sink);

        TestSummaries.Writer(logger).WriteInbound(Request("/healthz/"), 200, 1.0);

        Assert.Empty(sink.Events);
    }

    [Fact]
    public void WriteInbound_MinimalDetail_WritesShortKeys()
    {
        var sink = new CaptureSink();
        using var logger = LoggerOver(sink);

        TestSummaries.Writer(logger, new StepUpLoggingOptions { RequestSummaryDetail = RequestSummaryDetail.Minimal })
            .WriteInbound(Request("/api/orders"), 200, 1.0);

        var summary = Assert.Single(sink.Events);
        Assert.Equal("Inbound", Scalar(summary, "dir"));
        Assert.False(summary.Properties.ContainsKey("Direction"));
    }

    private static HttpContext ProxiedRequest()
    {
        var context = Request("/api/orders");
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        context.Connection.RemotePort = 51234;
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.9, 10.0.0.1";
        return context;
    }

    private static LogEvent InboundSummary(int status, StepUpLoggingOptions options)
    {
        var sink = new CaptureSink();
        using var logger = LoggerOver(sink);
        TestSummaries.Writer(logger, options).WriteInbound(ProxiedRequest(), status, 1.0);
        return Assert.Single(sink.Events);
    }

    private static object? ScalarOrNull(LogEvent logEvent, string name)
        => logEvent.Properties.TryGetValue(name, out var value) ? ((ScalarValue)value).Value : null;

    [Fact]
    public void WriteInbound_ClientErrorRequest_WritesTheFullClientIdentity_ByDefault()
    {
        var summary = InboundSummary(403, new StepUpLoggingOptions());

        Assert.Equal("203.0.113.7", Scalar(summary, "ClientIp"));
        Assert.Equal("198.51.100.9, 10.0.0.1", Scalar(summary, "ForwardedFor"));
        Assert.Equal(51234L, Scalar(summary, "RemotePort"));
    }

    [Fact]
    public void WriteInbound_SuccessfulRequest_WritesNoClientIdentity_ByDefault()
    {
        var summary = InboundSummary(200, new StepUpLoggingOptions());

        Assert.Null(ScalarOrNull(summary, "ClientIp"));
        Assert.Null(ScalarOrNull(summary, "ForwardedFor"));
        Assert.Null(ScalarOrNull(summary, "RemotePort"));
    }

    [Fact]
    public void WriteInbound_ClientErrorRequest_WritesNoClientIdentity_WhenOnClientErrorIsOff()
    {
        var summary = InboundSummary(404, new StepUpLoggingOptions { ClientIpLoggingOnClientError = ClientIpMode.Off });

        Assert.Null(ScalarOrNull(summary, "ClientIp"));
    }

    [Fact]
    public void WriteInbound_ClientErrorRequest_WritesTheTrustedForwardedAddressAndTheConnectionAddress()
    {
        var summary = InboundSummary(401, new StepUpLoggingOptions { TrustForwardedHeaders = true });

        Assert.Equal("198.51.100.9", Scalar(summary, "ClientIp"));
        Assert.Equal("203.0.113.7", Scalar(summary, "RemoteIp"));
    }

    [Fact]
    public void WriteInbound_ClientErrorRequest_MinimalDetail_WritesTheConnectionUnderShortKeys()
    {
        var summary = InboundSummary(401, new StepUpLoggingOptions { TrustForwardedHeaders = true, RequestSummaryDetail = RequestSummaryDetail.Minimal });

        Assert.Equal("203.0.113.7", Scalar(summary, "rip"));
        Assert.Equal(51234L, Scalar(summary, "rport"));
    }

    [Fact]
    public void WriteInbound_LoggerThrows_DoesNotThrow()
    {
        var writer = TestSummaries.Writer(new ThrowingLogger());

        var ex = Record.Exception(() => writer.WriteInbound(Request("/api/orders"), 200, 1.0));

        Assert.Null(ex);
    }

    [Fact]
    public void WriteOutbound_CompactDetail_WritesNoFullFields()
    {
        var sink = new CaptureSink();
        using var logger = LoggerOver(sink);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example/a?x=1");
        using var response = new HttpResponseMessage(HttpStatusCode.OK);

        TestSummaries.Writer(logger, new StepUpLoggingOptions { RequestSummaryDetail = RequestSummaryDetail.Compact })
            .WriteOutbound("billing", request, response, null, 1.0);

        var summary = Assert.Single(sink.Events);
        Assert.Equal("Outbound", Scalar(summary, "Direction"));
        Assert.Equal("/a", Scalar(summary, "Path"));
        Assert.Equal("billing", Scalar(summary, "HttpClientName"));
        Assert.False(summary.Properties.ContainsKey("Protocol"));
    }

    [Fact]
    public void WriteOutbound_LoggerThrows_DoesNotThrow()
    {
        var writer = TestSummaries.Writer(new ThrowingLogger());
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example/a");

        var ex = Record.Exception(() => writer.WriteOutbound("billing", request, null, new HttpRequestException("down"), 1.0));

        Assert.Null(ex);
    }
}
