using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Lukdrasil.StepUpLogging.Tests;

public class ImmediateLoggerExtensionsTests
{
    /// <summary>Creates a MEL ILogger backed by a Serilog pipeline that writes to <paramref name="collector"/>.</summary>
    private static Microsoft.Extensions.Logging.ILogger CreateMelLogger(CollectingSink collector)
    {
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(collector)
            .CreateLogger();
        var factory = new SerilogLoggerFactory(serilog, dispose: true);
        return factory.CreateLogger("Test");
    }

    private static void AssertIsImmediate(LogEvent evt)
    {
        Assert.True(evt.Properties.TryGetValue(LogProperties.IsImmediate, out var val),
            "IsImmediate property should be present");
        Assert.IsType<ScalarValue>(val);
        Assert.Equal(true, ((ScalarValue)val!).Value);
    }

    [Fact]
    public void LogImmediateInformation_SetsIsImmediate()
    {
        var collector = new CollectingSink();
        var logger = CreateMelLogger(collector);

        logger.LogImmediateInformation("hello {Value}", 42);

        Assert.Single(collector.Events);
        AssertIsImmediate(collector.Events[0]);
        Assert.Equal(LogEventLevel.Information, collector.Events[0].Level);
    }

    [Fact]
    public void LogImmediateWarning_SetsIsImmediate()
    {
        var collector = new CollectingSink();
        var logger = CreateMelLogger(collector);

        logger.LogImmediateWarning("quota near limit");

        Assert.Single(collector.Events);
        AssertIsImmediate(collector.Events[0]);
        Assert.Equal(LogEventLevel.Warning, collector.Events[0].Level);
    }

    [Fact]
    public void LogImmediateError_SetsIsImmediate()
    {
        var collector = new CollectingSink();
        var logger = CreateMelLogger(collector);

        logger.LogImmediateError("something failed");

        Assert.Single(collector.Events);
        AssertIsImmediate(collector.Events[0]);
        Assert.Equal(LogEventLevel.Error, collector.Events[0].Level);
    }

    [Fact]
    public void LogImmediateError_WithException_SetsIsImmediate()
    {
        var collector = new CollectingSink();
        var logger = CreateMelLogger(collector);
        var ex = new InvalidOperationException("boom");

        logger.LogImmediateError(ex, "error with exception");

        Assert.Single(collector.Events);
        AssertIsImmediate(collector.Events[0]);
        Assert.Equal(ex, collector.Events[0].Exception);
    }

    [Fact]
    public void LogImmediate_WithLevel_SetsIsImmediate()
    {
        var collector = new CollectingSink();
        var logger = CreateMelLogger(collector);

        logger.LogImmediate(LogLevel.Warning, "manual level");

        Assert.Single(collector.Events);
        AssertIsImmediate(collector.Events[0]);
    }

    [Fact]
    public void LogImmediate_WithLevelAndException_SetsIsImmediate()
    {
        var collector = new CollectingSink();
        var logger = CreateMelLogger(collector);
        var ex = new Exception("test");

        logger.LogImmediate(LogLevel.Error, ex, "with ex");

        Assert.Single(collector.Events);
        AssertIsImmediate(collector.Events[0]);
        Assert.Equal(ex, collector.Events[0].Exception);
    }

    [Fact]
    public void BeginImmediateScope_MarksAllLogsInsideScope()
    {
        var collector = new CollectingSink();
        var logger = CreateMelLogger(collector);

        using (logger.BeginImmediateScope())
        {
            logger.LogInformation("step 1");
            logger.LogInformation("step 2");
        }

        Assert.Equal(2, collector.Events.Count);
        AssertIsImmediate(collector.Events[0]);
        AssertIsImmediate(collector.Events[1]);
    }

    [Fact]
    public void BeginImmediateScope_DoesNotMarkLogsOutsideScope()
    {
        var collector = new CollectingSink();
        var logger = CreateMelLogger(collector);

        logger.LogInformation("before scope");

        using (logger.BeginImmediateScope())
        {
            logger.LogInformation("inside scope");
        }

        logger.LogInformation("after scope");

        Assert.Equal(3, collector.Events.Count);
        // Outside the scope: no IsImmediate property (or not true)
        Assert.False(
            collector.Events[0].Properties.TryGetValue(LogProperties.IsImmediate, out var before)
            && before is ScalarValue sv0 && sv0.Value is bool b0 && b0,
            "Event before scope should not have IsImmediate=true");
        AssertIsImmediate(collector.Events[1]);
        Assert.False(
            collector.Events[2].Properties.TryGetValue(LogProperties.IsImmediate, out var after)
            && after is ScalarValue sv2 && sv2.Value is bool b2 && b2,
            "Event after scope should not have IsImmediate=true");
    }

    [Fact]
    public void NormalLog_DoesNotHaveIsImmediate()
    {
        var collector = new CollectingSink();
        var logger = CreateMelLogger(collector);

        logger.LogInformation("ordinary log");

        Assert.Single(collector.Events);
        Assert.False(
            collector.Events[0].Properties.TryGetValue(LogProperties.IsImmediate, out var val)
            && val is ScalarValue sv && sv.Value is bool b && b);
    }
}
