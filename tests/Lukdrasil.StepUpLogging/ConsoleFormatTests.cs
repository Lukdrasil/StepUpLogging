using System;
using System.Diagnostics;
using System.IO;
using Serilog.Events;
using Serilog.Parsing;

namespace Lukdrasil.StepUpLogging.Tests;

public class ConsoleFormatTests
{
    private static readonly ActivityTraceId TraceId = ActivityTraceId.CreateFromString("c4fed5a2d96d1640c013ae014f5d162d");

    private static string Format(ConsoleLogFormat format, LogEvent logEvent)
    {
        using var output = new StringWriter();
        StepUpLoggingExtensions.CreateConsoleFormatter(format).Format(logEvent, output);
        return output.ToString();
    }

    private static LogEvent Event(Exception? exception = null) => new(
        new DateTimeOffset(2026, 10, 17, 15, 54, 7, 221, TimeSpan.Zero),
        LogEventLevel.Error,
        exception,
        new MessageTemplateParser().Parse("Order {OrderId} failed for {Customer}"),
        [
            new LogEventProperty("OrderId", new ScalarValue(42)),
            new LogEventProperty("Customer", new ScalarValue("alice")),
            new LogEventProperty("SourceContext", new ScalarValue("MyApp.Orders")),
            new LogEventProperty("Unrendered", new ScalarValue("not-in-the-message")),
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
    public void ConsoleFormat_DefaultsToJson()
    {
        Assert.Equal(ConsoleLogFormat.Json, new StepUpLoggingOptions().ConsoleFormat);
    }
}
