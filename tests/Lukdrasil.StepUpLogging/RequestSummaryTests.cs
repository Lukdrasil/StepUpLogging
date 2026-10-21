using System.Diagnostics;
using System.Text.RegularExpressions;
using Serilog;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Tests;

public class RequestSummaryTests
{
    private const string Template = "{Direction} {Method} {Path} {StatusCode} {ElapsedMs} ms {ClientKind} {UserAgent}";

    private static readonly CompiledRedactionPatterns NoPatterns = new([]);

    private static readonly Serilog.ILogger Logger = new LoggerConfiguration().CreateLogger();

    private static RequestSummary Inbound(string? userAgent = "curl/8", int? statusCode = 200, CompiledRedactionPatterns? patterns = null)
        => new("Inbound", "GET", "/a", statusCode, 1.5, userAgent, patterns ?? NoPatterns);

    private static LogEvent Event(RequestSummary summary) => summary.ToLogEvent(LogEventLevel.Information, Logger);

    private static object? Scalar(LogEvent logEvent, string name) => ((ScalarValue)logEvent.Properties[name]).Value;

    [Fact]
    public void ToLogEvent_UsesTheDirectionTemplate()
    {
        var logEvent = Event(Inbound());

        Assert.Equal(Template, logEvent.MessageTemplate.Text);
        Assert.Equal("Inbound", Scalar(logEvent, "Direction"));
        Assert.Equal("Tool", Scalar(logEvent, "ClientKind"));
        var text = logEvent.RenderMessage();
        Assert.Contains("Inbound", text, StringComparison.Ordinal);
        Assert.Contains("curl/8", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ToLogEvent_BlankUserAgent_RendersNone()
    {
        var logEvent = Event(Inbound(userAgent: "  "));

        Assert.Equal("(none)", Scalar(logEvent, "UserAgent"));
        Assert.Equal("None", Scalar(logEvent, "ClientKind"));
        Assert.Contains("(none)", logEvent.RenderMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void ToLogEvent_NoStatusCode_LeavesStatusCodeOut()
    {
        var logEvent = Event(Inbound(statusCode: null));

        Assert.False(logEvent.Properties.ContainsKey("StatusCode"));
        Assert.Equal(Template, logEvent.MessageTemplate.Text);
    }

    [Fact]
    public void WithTrue_False_LeavesTheFieldOut()
    {
        var logEvent = Event(Inbound().WithTrue("Aborted", false));

        Assert.False(logEvent.Properties.ContainsKey("Aborted"));
    }

    [Fact]
    public void WithTrue_True_WritesTrue()
    {
        var logEvent = Event(Inbound().WithTrue("Aborted", true));

        Assert.Equal(true, Scalar(logEvent, "Aborted"));
    }

    [Fact]
    public void With_NullLength_LeavesTheFieldOut()
    {
        var logEvent = Event(Inbound().With("RequestContentLength", (long?)null));

        Assert.False(logEvent.Properties.ContainsKey("RequestContentLength"));
    }

    [Fact]
    public void With_ZeroLength_WritesZero()
    {
        var logEvent = Event(Inbound().With("RequestContentLength", 0L));

        Assert.Equal(0L, Scalar(logEvent, "RequestContentLength"));
    }

    [Fact]
    public void ToLogEvent_InsideAnActivity_CarriesItsTraceAndSpan()
    {
        using var activity = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();

        var logEvent = Event(Inbound());

        Assert.Equal(activity.TraceId, logEvent.TraceId);
        Assert.Equal(activity.SpanId, logEvent.SpanId);
    }

    [Fact]
    public void RawUserAgent_IsClassifiedBeforeItIsRedacted()
    {
        var wholeHeader = new CompiledRedactionPatterns([new Regex("^.*secret-.*$")]);

        var logEvent = Event(Inbound(userAgent: "curl/8 secret-x", patterns: wholeHeader));

        Assert.Equal("Tool", Scalar(logEvent, "ClientKind"));
        Assert.Equal("[REDACTED]", Scalar(logEvent, "UserAgent"));
    }

    [Fact]
    public void NoPatterns_KeepsTheUserAgentAsGiven()
    {
        var logEvent = Event(Inbound(userAgent: "already [REDACTED]"));

        Assert.Equal("already [REDACTED]", Scalar(logEvent, "UserAgent"));
    }
}
