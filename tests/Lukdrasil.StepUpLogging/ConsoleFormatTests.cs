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
    {
        using var output = new StringWriter();
        StepUpLoggingExtensions.CreateConsoleFormatter(format, textProperties).Format(logEvent, output);
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
        Assert.Contains(" ERR] c4fed5a2d96d1640c013ae014f5d162d MyApp.Orders: Order 42 failed for alice", line);
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
    public void ConsoleFormat_DefaultsToJson()
    {
        Assert.Equal(ConsoleLogFormat.Json, new StepUpLoggingOptions().ConsoleFormat);
    }
}
