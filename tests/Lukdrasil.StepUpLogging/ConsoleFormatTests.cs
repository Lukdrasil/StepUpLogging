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
    {
        using var output = new StringWriter();
        StepUpLoggingExtensions.CreateConsoleFormatter(format, textProperties, traceFormat).Format(logEvent, output);
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
    public void Text_WritesOneLineWithLevelTraceIdSourceContextAndMessage()
    {
        var line = Format(ConsoleLogFormat.Text, Event());

        Assert.EndsWith(Environment.NewLine, line);
        Assert.Contains(" ERR] TraceId=c4fed5a2d96d1640c013ae014f5d162d MyApp.Orders: Order 42 failed for alice", line);
        Assert.DoesNotContain("not-in-the-message", line);
        Assert.DoesNotContain("{", line);
    }

    [Fact]
    public void Text_WritesTheExceptionAfterTheMessage()
    {
        var text = Format(ConsoleLogFormat.Text, Event(new InvalidOperationException("token mint refused")));

        Assert.Contains("Order 42 failed for alice" + Environment.NewLine + "System.InvalidOperationException: token mint refused", text);
    }

    [Fact]
    public void Text_WritesAListedPropertyAfterTheMessage()
    {
        var line = Format(ConsoleLogFormat.Text, Event(null, new LogEventProperty("Jti", new ScalarValue("4ebece0627d94b5f9f37c969bb17d27f"))), "Jti");

        Assert.EndsWith("Order 42 failed for alice Jti=4ebece0627d94b5f9f37c969bb17d27f" + Environment.NewLine, line);
    }

    [Fact]
    public void Text_WritesListedPropertiesInListOrder_AndSkipsAbsentOnes()
    {
        var line = Format(
            ConsoleLogFormat.Text,
            Event(null, new LogEventProperty("UserId", new ScalarValue(7)), new LogEventProperty("Jti", new ScalarValue("j-1"))),
            "Jti", "ClientId", "UserId");

        Assert.EndsWith("Order 42 failed for alice Jti=j-1 UserId=7" + Environment.NewLine, line);
    }

    [Fact]
    public void Text_ListedPropertyThenException_ExceptionFollowsTheLine()
    {
        var text = Format(ConsoleLogFormat.Text, Event(new InvalidOperationException("token mint refused"), new LogEventProperty("Jti", new ScalarValue("j-1"))), "Jti");

        Assert.Contains("Order 42 failed for alice Jti=j-1" + Environment.NewLine + "System.InvalidOperationException: token mint refused", text);
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

        Assert.EndsWith("Login for bob\\n[12:00:00.000 ERR] forged Jti=j-1\\r\\nforged" + Environment.NewLine, text);
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

        Assert.EndsWith(" INF] Application started" + Environment.NewLine, line);
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

        Assert.EndsWith(" INF] Microsoft.Hosting.Lifetime: Application started" + Environment.NewLine, line);
    }

    [Fact]
    public void Text_Traceparent_WritesW3CTokenWithFlags()
    {
        var line = Format(ConsoleLogFormat.Text, ConsoleTraceFormat.Traceparent, Event(null, new LogEventProperty("TraceFlags", new ScalarValue("1"))));

        Assert.Contains(" ERR] traceparent=00-c4fed5a2d96d1640c013ae014f5d162d-c692a71cf2b6dbbb-01 MyApp.Orders:", line);
    }

    [Fact]
    public void Text_Traceparent_WithoutTraceFlags_Writes00()
    {
        var line = Format(ConsoleLogFormat.Text, ConsoleTraceFormat.Traceparent, Event());

        Assert.Contains(" ERR] traceparent=00-c4fed5a2d96d1640c013ae014f5d162d-c692a71cf2b6dbbb-00 MyApp.Orders:", line);
    }

    [Fact]
    public void Text_Traceparent_WithUnparseableTraceFlags_Writes00()
    {
        var line = Format(ConsoleLogFormat.Text, ConsoleTraceFormat.Traceparent, Event(null, new LogEventProperty("TraceFlags", new ScalarValue("x"))));

        Assert.Contains(" ERR] traceparent=00-c4fed5a2d96d1640c013ae014f5d162d-c692a71cf2b6dbbb-00 MyApp.Orders:", line);
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

        Assert.EndsWith(" INF] TraceId=c4fed5a2d96d1640c013ae014f5d162d Application started" + Environment.NewLine, line);
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

        Assert.EndsWith(" INF] Application started" + Environment.NewLine, line);
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

        Assert.EndsWith($" INF] traceparent=00-{activity.TraceId.ToHexString()}-{activity.SpanId.ToHexString()}-01 Application started" + Environment.NewLine, line);
    }

    [Fact]
    public void ConsoleFormat_DefaultsToJson()
    {
        Assert.Equal(ConsoleLogFormat.Json, new StepUpLoggingOptions().ConsoleFormat);
    }
}
