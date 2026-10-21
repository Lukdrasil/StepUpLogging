using System.Net;
using System.Text.RegularExpressions;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Tests;

public class OutboundRequestSummaryHandlerTests
{
    private static readonly CompiledRedactionPatterns NoPatterns = new([]);

    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request);
    }

    private sealed class Harness : IDisposable
    {
        private readonly StepUpLoggingController _controller;

        public Harness(
            Func<HttpRequestMessage, HttpResponseMessage>? respond = null,
            string clientName = "billing",
            RequestSummaryDetail detail = RequestSummaryDetail.Full,
            CompiledRedactionPatterns? patterns = null)
        {
            _controller = new StepUpLoggingController(new StepUpLoggingOptions(), new LoggerConfiguration().WriteTo.Sink(Sink).CreateLogger());
            var handler = new OutboundRequestSummaryHandler(clientName, _controller, patterns ?? NoPatterns, detail)
            {
                InnerHandler = new StubHandler(respond ?? (_ => new HttpResponseMessage(HttpStatusCode.OK))),
            };
            Invoker = new HttpMessageInvoker(handler);
        }

        public CaptureSink Sink { get; } = new();

        public HttpMessageInvoker Invoker { get; }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
            => Invoker.SendAsync(request, TestContext.Current.CancellationToken);

        public void Dispose()
        {
            Invoker.Dispose();
            _controller.Dispose();
        }
    }

    private static HttpRequestMessage Get(string uri = "https://api.example/a") => new(HttpMethod.Get, uri);

    private static object? Scalar(LogEvent logEvent, string name) => ((ScalarValue)logEvent.Properties[name]).Value;

    [Fact]
    public async Task SuccessfulCall_WritesOneOutboundSummaryWithClientName()
    {
        using var harness = new Harness(clientName: "billing");

        using var response = await harness.SendAsync(Get());

        var summary = Assert.Single(harness.Sink.Events);
        Assert.Equal("Outbound", Scalar(summary, "Direction"));
        Assert.Equal("billing", Scalar(summary, "HttpClientName"));
        Assert.Equal(200, Scalar(summary, "StatusCode"));
        Assert.Equal(true, Scalar(summary, "IsRequestSummary"));
    }

    [Fact]
    public async Task Compact_WritesExactlyTheCompactFields()
    {
        using var harness = new Harness(detail: RequestSummaryDetail.Compact);

        using var response = await harness.SendAsync(Get());

        var summary = Assert.Single(harness.Sink.Events);
        Assert.Equal(
            ["Direction", "Method", "Path", "StatusCode", "ElapsedMs", "ClientKind", "UserAgent", "HttpClientName", "Host", "IsRequestSummary"],
            summary.Properties.Keys);
    }

    [Fact]
    public async Task Full_AddsProtocolAndContentLengths()
    {
        using var harness = new Harness(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Version = HttpVersion.Version20,
            Content = new ByteArrayContent(new byte[7]),
        });
        using var request = Get();
        request.Content = new ByteArrayContent(new byte[5]);

        using var response = await harness.SendAsync(request);

        var summary = Assert.Single(harness.Sink.Events);
        Assert.Equal("HTTP/2", Scalar(summary, "Protocol"));
        Assert.Equal(5L, Scalar(summary, "RequestContentLength"));
        Assert.Equal(7L, Scalar(summary, "ResponseContentLength"));
    }

    [Fact]
    public async Task FailingCall_WritesExceptionTypeWithoutStatusCode_AndRethrowsTheSameException()
    {
        var failure = new HttpRequestException("connection refused");
        using var harness = new Harness(_ => throw failure);

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => harness.SendAsync(Get()));

        Assert.Same(failure, thrown);
        var summary = Assert.Single(harness.Sink.Events);
        Assert.Equal("System.Net.Http.HttpRequestException", Scalar(summary, "ExceptionType"));
        Assert.False(summary.Properties.ContainsKey("StatusCode"));
    }

    [Fact]
    public async Task FailingCall_Compact_LeavesExceptionTypeOut()
    {
        using var harness = new Harness(_ => throw new HttpRequestException("connection refused"), detail: RequestSummaryDetail.Compact);

        await Assert.ThrowsAsync<HttpRequestException>(() => harness.SendAsync(Get()));

        var summary = Assert.Single(harness.Sink.Events);
        Assert.False(summary.Properties.ContainsKey("ExceptionType"));
    }

    [Fact]
    public async Task NoUserAgentHeader_RendersNone()
    {
        using var harness = new Harness();

        using var response = await harness.SendAsync(Get());

        var summary = Assert.Single(harness.Sink.Events);
        Assert.Equal("(none)", Scalar(summary, "UserAgent"));
        Assert.Equal("None", Scalar(summary, "ClientKind"));
    }

    [Fact]
    public async Task CurlUserAgent_IsTool()
    {
        using var harness = new Harness();
        using var request = Get();
        request.Headers.UserAgent.ParseAdd("curl/8.5.0");

        using var response = await harness.SendAsync(request);

        var summary = Assert.Single(harness.Sink.Events);
        Assert.Equal("curl/8.5.0", Scalar(summary, "UserAgent"));
        Assert.Equal("Tool", Scalar(summary, "ClientKind"));
    }

    [Fact]
    public async Task UriWithQuery_LogsPathWithoutQuery()
    {
        using var harness = new Harness();

        using var response = await harness.SendAsync(Get("https://api:8443/a?token=x"));

        var summary = Assert.Single(harness.Sink.Events);
        Assert.Equal("/a", Scalar(summary, "Path"));
        Assert.Equal("api:8443", Scalar(summary, "Host"));
        Assert.All(summary.Properties, p => Assert.DoesNotContain("token=x", p.Value.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task MatchingPattern_RedactsPathHostAndUserAgent()
    {
        using var harness = new Harness(patterns: new CompiledRedactionPatterns([new Regex("secret-[A-Za-z0-9]+")]));
        using var request = Get("https://secret-host:8443/v1/secret-p1");
        request.Headers.TryAddWithoutValidation("User-Agent", "agent/1 secret-ua");

        using var response = await harness.SendAsync(request);

        var summary = Assert.Single(harness.Sink.Events);
        Assert.All(
            new[] { "Path", "Host", "UserAgent" },
            name => Assert.Contains("[REDACTED]", (string)Scalar(summary, name)!, StringComparison.Ordinal));
        Assert.All(summary.Properties, p => Assert.DoesNotContain("secret-", p.Value.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void SynchronousSend_AlsoWritesASummary()
    {
        using var harness = new Harness();

        using var response = harness.Invoker.Send(Get(), TestContext.Current.CancellationToken);

        var summary = Assert.Single(harness.Sink.Events);
        Assert.Equal("Outbound", Scalar(summary, "Direction"));
    }

    [Fact]
    public async Task EmptyClientName_IsDefault()
    {
        using var harness = new Harness(clientName: "");

        using var response = await harness.SendAsync(Get());

        var summary = Assert.Single(harness.Sink.Events);
        Assert.Equal("(default)", Scalar(summary, "HttpClientName"));
    }
}
