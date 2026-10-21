using System;
using System.Diagnostics;
using System.IO;
using Serilog.Events;
using Serilog.Parsing;

namespace Lukdrasil.StepUpLogging.Tests;

public class ConsoleFormatTests
{
    private static readonly ActivityTraceId TraceId = ActivityTraceId.CreateFromString("c4fed5a2d96d1640c013ae014f5d162d");

    private static string Format(ConsoleLogFormat format, LogEvent logEvent, params string[] textProperties)
        => Format(format, ConsoleTraceFormat.TraceId, logEvent, textProperties);

    private static string Format(ConsoleLogFormat format, ConsoleTraceFormat traceFormat, LogEvent logEvent, params string[] textProperties)
        => Format(format, traceFormat, RequestSummaryDetail.Full, logEvent, textProperties);

    private static string Format(ConsoleLogFormat format, ConsoleTraceFormat traceFormat, RequestSummaryDetail detail, LogEvent logEvent, params string[] textProperties)
    {
        using var output = new StringWriter();
        StepUpLoggingExtensions.CreateConsoleFormatter(format, textProperties, traceFormat, detail).Format(logEvent, output);
        return output.ToString();
    }

    private static LogEvent Event(Exception? exception = null, params LogEventProperty[] extra) => new(
        new DateTimeOffset(2026, 10, 17, 15, 54, 7, 221, TimeSpan.Zero),
        LogEventLevel.Error,
        exception,
        new MessageTemplateParser().Parse("Order {OrderId} failed for {Customer}"),
        [
            new LogEventProperty("OrderId", new ScalarValue(42)),
            new LogEventProperty("Customer", new ScalarValue("alice")),
            new LogEventProperty("SourceContext", new ScalarValue("MyApp.Orders")),
            new LogEventProperty("Unrendered", new ScalarValue("not-in-the-message")),
            .. extra,
        ],
        TraceId,
        ActivitySpanId.CreateFromString("c692a71cf2b6dbbb"));

    [Fact]
    public void Json_WritesCompactJson()
    {
        var line = Format(ConsoleLogFormat.Json, Event());

        Assert.StartsWith("{\"@t\":", line);
        Assert.Contains("\"Unrendered\":\"not-in-the-message\"", line);
    }

    [Fact]
    public void Text_WritesEveryItemAsKeyValue()
    {
        var line = Format(ConsoleLogFormat.Text, Event());

        Assert.Equal(
            "Timestamp=15:54:07.221 Level=ERR TraceId=c4fed5a2d96d1640c013ae014f5d162d SourceContext=MyApp.Orders Message=\"Order 42 failed for alice\"" + Environment.NewLine,
            line);
        Assert.DoesNotContain("not-in-the-message", line);
        Assert.DoesNotContain("{", line);
    }

    [Fact]
    public void Text_WritesTheExceptionAsTheLastKeyOnTheSameLine()
    {
        var text = Format(ConsoleLogFormat.Text, Event(new InvalidOperationException("token mint refused")));

        Assert.EndsWith("Message=\"Order 42 failed for alice\" Exception=\"System.InvalidOperationException: token mint refused\"" + Environment.NewLine, text);
    }

    [Fact]
    public void Text_WritesAListedPropertyAfterTheMessage()
    {
        var line = Format(ConsoleLogFormat.Text, Event(null, new LogEventProperty("Jti", new ScalarValue("4ebece0627d94b5f9f37c969bb17d27f"))), "Jti");

        Assert.EndsWith("Message=\"Order 42 failed for alice\" Jti=4ebece0627d94b5f9f37c969bb17d27f" + Environment.NewLine, line);
    }

    [Fact]
    public void Text_WritesListedPropertiesInListOrder_AndSkipsAbsentOnes()
    {
        var line = Format(
            ConsoleLogFormat.Text,
            Event(null, new LogEventProperty("UserId", new ScalarValue(7)), new LogEventProperty("Jti", new ScalarValue("j-1"))),
            "Jti", "ClientId", "UserId");

        Assert.EndsWith("Message=\"Order 42 failed for alice\" Jti=j-1 UserId=7" + Environment.NewLine, line);
    }

    [Fact]
    public void Text_ListedPropertyThenException_ExceptionFollowsTheProperty()
    {
        var text = Format(ConsoleLogFormat.Text, Event(new InvalidOperationException("token mint refused"), new LogEventProperty("Jti", new ScalarValue("j-1"))), "Jti");

        Assert.EndsWith("Message=\"Order 42 failed for alice\" Jti=j-1 Exception=\"System.InvalidOperationException: token mint refused\"" + Environment.NewLine, text);
    }

    [Fact]
    public void Text_MultiLineException_StaysOnOneLine()
    {
        var exception = new InvalidOperationException("outer", new InvalidOperationException("inner"));

        var text = Format(ConsoleLogFormat.Text, Event(exception));

        Assert.Single(text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains(" Exception=\"System.InvalidOperationException: outer\\n ---> System.InvalidOperationException: inner\\n", text.Replace("\\r\\n", "\\n"));
    }

    [Fact]
    public void Json_IgnoresTheListedProperties()
    {
        var line = Format(ConsoleLogFormat.Json, Event(null, new LogEventProperty("Jti", new ScalarValue("j-1"))), "Jti");

        Assert.DoesNotContain("Jti=", line);
    }

    [Fact]
    public void Text_NewlinesInPropertyValues_AreEscaped_SoNoLineIsForged()
    {
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            null,
            new MessageTemplateParser().Parse("Login for {User}"),
            [
                new LogEventProperty("User", new ScalarValue("bob\n[12:00:00.000 ERR] forged")),
                new LogEventProperty("Jti", new ScalarValue("j-1\r\nforged")),
            ]);

        var text = Format(ConsoleLogFormat.Text, logEvent, "Jti");

        Assert.EndsWith("Message=\"Login for bob\\n[12:00:00.000 ERR] forged\" Jti=\"j-1\\r\\nforged\"" + Environment.NewLine, text);
        Assert.Single(text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Text_WithoutTraceIdAndSourceContext_WritesNoGap()
    {
        var logEvent = new LogEvent(
            new DateTimeOffset(2026, 10, 17, 15, 54, 7, 221, TimeSpan.Zero),
            LogEventLevel.Information,
            null,
            new MessageTemplateParser().Parse("Application started"),
            []);

        var line = Format(ConsoleLogFormat.Text, logEvent);

        Assert.Equal("Timestamp=15:54:07.221 Level=INF Message=\"Application started\"" + Environment.NewLine, line);
    }

    [Fact]
    public void Text_WithSourceContextOnly_WritesItWithoutAGap()
    {
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            null,
            new MessageTemplateParser().Parse("Application started"),
            [new LogEventProperty("SourceContext", new ScalarValue("Microsoft.Hosting.Lifetime"))]);

        var line = Format(ConsoleLogFormat.Text, logEvent);

        Assert.EndsWith(" Level=INF SourceContext=Microsoft.Hosting.Lifetime Message=\"Application started\"" + Environment.NewLine, line);
    }

    [Fact]
    public void Text_Traceparent_WritesW3CTokenWithFlags()
    {
        var line = Format(ConsoleLogFormat.Text, ConsoleTraceFormat.Traceparent, Event(null, new LogEventProperty("TraceFlags", new ScalarValue("1"))));

        Assert.Contains(" Level=ERR traceparent=00-c4fed5a2d96d1640c013ae014f5d162d-c692a71cf2b6dbbb-01 SourceContext=MyApp.Orders ", line);
    }

    [Fact]
    public void Text_Traceparent_WithoutTraceFlags_Writes00()
    {
        var line = Format(ConsoleLogFormat.Text, ConsoleTraceFormat.Traceparent, Event());

        Assert.Contains(" Level=ERR traceparent=00-c4fed5a2d96d1640c013ae014f5d162d-c692a71cf2b6dbbb-00 SourceContext=MyApp.Orders ", line);
    }

    [Fact]
    public void Text_Traceparent_WithUnparseableTraceFlags_Writes00()
    {
        var line = Format(ConsoleLogFormat.Text, ConsoleTraceFormat.Traceparent, Event(null, new LogEventProperty("TraceFlags", new ScalarValue("x"))));

        Assert.Contains(" Level=ERR traceparent=00-c4fed5a2d96d1640c013ae014f5d162d-c692a71cf2b6dbbb-00 SourceContext=MyApp.Orders ", line);
    }

    [Fact]
    public void Text_Traceparent_WithoutSpanId_FallsBackToTraceId()
    {
        var logEvent = new LogEvent(
            new DateTimeOffset(2026, 10, 17, 15, 54, 7, 221, TimeSpan.Zero),
            LogEventLevel.Information,
            null,
            new MessageTemplateParser().Parse("Application started"),
            [],
            TraceId,
            default);

        var line = Format(ConsoleLogFormat.Text, ConsoleTraceFormat.Traceparent, logEvent);

        Assert.EndsWith(" Level=INF TraceId=c4fed5a2d96d1640c013ae014f5d162d Message=\"Application started\"" + Environment.NewLine, line);
        Assert.DoesNotContain("traceparent=", line);
    }

    [Fact]
    public void Text_Traceparent_WithoutTraceId_WritesNoTraceToken()
    {
        var logEvent = new LogEvent(
            new DateTimeOffset(2026, 10, 17, 15, 54, 7, 221, TimeSpan.Zero),
            LogEventLevel.Information,
            null,
            new MessageTemplateParser().Parse("Application started"),
            []);

        var line = Format(ConsoleLogFormat.Text, ConsoleTraceFormat.Traceparent, logEvent);

        Assert.EndsWith(" Level=INF Message=\"Application started\"" + Environment.NewLine, line);
        Assert.DoesNotContain("TraceId=", line);
        Assert.DoesNotContain("traceparent=", line);
    }

    [Fact]
    public void Text_Traceparent_FlagsFromActivityContextEnricher()
    {
        using var activity = new Activity("Request");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();
        activity.ActivityTraceFlags = ActivityTraceFlags.Recorded;
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            null,
            new MessageTemplateParser().Parse("Application started"),
            [],
            activity.TraceId,
            activity.SpanId);
        new ActivityContextEnricher().Enrich(logEvent, new SimpleLogEventPropertyFactory());

        var line = Format(ConsoleLogFormat.Text, ConsoleTraceFormat.Traceparent, logEvent);

        Assert.EndsWith($" Level=INF traceparent=00-{activity.TraceId.ToHexString()}-{activity.SpanId.ToHexString()}-01 Message=\"Application started\"" + Environment.NewLine, line);
    }

    [Fact]
    public void Text_ValueWithoutSpaceOrQuote_IsNotQuoted()
    {
        var logEvent = new LogEvent(
            new DateTimeOffset(2026, 10, 17, 15, 54, 7, 221, TimeSpan.Zero),
            LogEventLevel.Information,
            null,
            new MessageTemplateParser().Parse("Started"),
            []);

        var line = Format(ConsoleLogFormat.Text, logEvent);

        Assert.Equal("Timestamp=15:54:07.221 Level=INF Message=Started" + Environment.NewLine, line);
    }

    [Fact]
    public void Text_QuotesAndBackslashesInValues_AreEscaped()
    {
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            null,
            new MessageTemplateParser().Parse("Path {Path}"),
            [
                new LogEventProperty("Path", new ScalarValue("C:\\temp\\\"x\"")),
                new LogEventProperty("Jti", new ScalarValue("a=b")),
                new LogEventProperty("UserId", new ScalarValue("")),
            ]);

        var line = Format(ConsoleLogFormat.Text, logEvent, "Jti", "UserId");

        Assert.EndsWith("Message=\"Path C:\\\\temp\\\\\\\"x\\\"\" Jti=\"a=b\" UserId=\"\"" + Environment.NewLine, line);
    }

    [Fact]
    public void Text_ControlCharactersInValues_AreQuotedAndEscaped()
    {
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            null,
            new MessageTemplateParser().Parse("Started"),
            [new LogEventProperty("Jti", new ScalarValue("a\tb\u0001c\u007f"))]);

        var line = Format(ConsoleLogFormat.Text, logEvent, "Jti");

        Assert.EndsWith(" Jti=\"a\\tb\\u0001c\\u007f\"" + Environment.NewLine, line);
    }

    [Fact]
    public void Text_Minimal_WritesShortLineKeys()
    {
        var line = Format(
            ConsoleLogFormat.Text,
            ConsoleTraceFormat.TraceId,
            RequestSummaryDetail.Minimal,
            Event(new InvalidOperationException("token mint refused"), new LogEventProperty("jti", new ScalarValue("j-1"))),
            "jti");

        Assert.Equal(
            "t=15:54:07.221 l=ERR tid=c4fed5a2d96d1640c013ae014f5d162d src=MyApp.Orders msg=\"Order 42 failed for alice\" jti=j-1 ex=\"System.InvalidOperationException: token mint refused\"" + Environment.NewLine,
            line);
    }

    [Fact]
    public void Text_Minimal_Traceparent_WritesTp()
    {
        var line = Format(ConsoleLogFormat.Text, ConsoleTraceFormat.Traceparent, RequestSummaryDetail.Minimal, Event(null, new LogEventProperty("TraceFlags", new ScalarValue("1"))));

        Assert.Contains(" l=ERR tp=00-c4fed5a2d96d1640c013ae014f5d162d-c692a71cf2b6dbbb-01 src=MyApp.Orders ", line);
    }

    [Fact]
    public void Text_Minimal_Traceparent_WithoutSpanId_FallsBackToTid()
    {
        var logEvent = new LogEvent(
            new DateTimeOffset(2026, 10, 17, 15, 54, 7, 221, TimeSpan.Zero),
            LogEventLevel.Information,
            null,
            new MessageTemplateParser().Parse("Application started"),
            [],
            TraceId,
            default);

        var line = Format(ConsoleLogFormat.Text, ConsoleTraceFormat.Traceparent, RequestSummaryDetail.Minimal, logEvent);

        Assert.Equal("t=15:54:07.221 l=INF tid=c4fed5a2d96d1640c013ae014f5d162d msg=\"Application started\"" + Environment.NewLine, line);
    }

    [Theory]
    [InlineData(RequestSummaryDetail.Full)]
    [InlineData(RequestSummaryDetail.Compact)]
    public void Text_FullAndCompact_KeepTheLongLineKeys(RequestSummaryDetail detail)
    {
        var logEvent = Event(new InvalidOperationException("token mint refused"), new LogEventProperty("Jti", new ScalarValue("j-1")), new LogEventProperty("TraceFlags", new ScalarValue("1")));

        Assert.Equal(
            "Timestamp=15:54:07.221 Level=ERR TraceId=c4fed5a2d96d1640c013ae014f5d162d SourceContext=MyApp.Orders Message=\"Order 42 failed for alice\" Jti=j-1 Exception=\"System.InvalidOperationException: token mint refused\"" + Environment.NewLine,
            Format(ConsoleLogFormat.Text, ConsoleTraceFormat.TraceId, detail, logEvent, "Jti"));
        Assert.Equal(
            "Timestamp=15:54:07.221 Level=ERR traceparent=00-c4fed5a2d96d1640c013ae014f5d162d-c692a71cf2b6dbbb-01 SourceContext=MyApp.Orders Message=\"Order 42 failed for alice\" Jti=j-1 Exception=\"System.InvalidOperationException: token mint refused\"" + Environment.NewLine,
            Format(ConsoleLogFormat.Text, ConsoleTraceFormat.Traceparent, detail, logEvent, "Jti"));
    }

    [Fact]
    public void Json_Minimal_WritesCompactJson()
    {
        var line = Format(ConsoleLogFormat.Json, ConsoleTraceFormat.TraceId, RequestSummaryDetail.Minimal, Event());

        Assert.StartsWith("{\"@t\":", line);
        Assert.Contains("\"Unrendered\":\"not-in-the-message\"", line);
    }

    [Fact]
    public void ConsoleFormat_DefaultsToJson()
    {
        Assert.Equal(ConsoleLogFormat.Json, new StepUpLoggingOptions().ConsoleFormat);
    }
}
