using System;
using System.Threading.Tasks;
using Serilog.Events;
using Serilog.Parsing;

namespace Lukdrasil.StepUpLogging.Tests;

public class StepUpTriggerSinkTests
{
    [Fact]
    public async Task Emit_Error_TriggersController()
    {
        var opts = new StepUpLoggingOptions
        {
            BaseLevel = "Warning",
            StepUpLevel = "Information",
            DurationSeconds = 2
        };
        using var controller = new StepUpLoggingController(opts);
        using var sink = new StepUpTriggerSink(controller);

        Assert.False(controller.IsSteppedUp);

        var parser = new MessageTemplateParser();
        var logEvent = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error, exception: null,
            messageTemplate: parser.Parse("err"),
            properties: Array.Empty<LogEventProperty>());

        sink.Emit(logEvent);

        // Allow background channel processor to run
        await Task.Delay(100);

        Assert.True(controller.IsSteppedUp);
    }

    [Fact]
    public async Task Emit_Information_DoesNotTrigger()
    {
        var opts = new StepUpLoggingOptions
        {
            BaseLevel = "Warning",
            StepUpLevel = "Information",
            DurationSeconds = 2
        };
        using var controller = new StepUpLoggingController(opts);
        using var sink = new StepUpTriggerSink(controller);

        Assert.False(controller.IsSteppedUp);

        var parser = new MessageTemplateParser();
        var logEvent = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, exception: null,
            messageTemplate: parser.Parse("info"),
            properties: Array.Empty<LogEventProperty>());

        sink.Emit(logEvent);
        await Task.Delay(100);

        Assert.False(controller.IsSteppedUp);
    }
}
