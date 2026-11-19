using System;
using System.Linq;
using Microsoft.Extensions.Time.Testing;
using Serilog;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Tests;

public class StepUpControllerGenerationTests
{
    private static StepUpLoggingOptions AutoOptions() => new()
    {
        Mode = StepUpMode.Auto,
        BaseLevel = "Warning",
        StepUpLevel = "Information",
        // Large duration so the real timer never fires during the test; step-down is driven manually.
        DurationSeconds = 300
    };

    private static int StepDowns(CollectingSink output)
        => output.Events.Count(e => e.MessageTemplate.Text.StartsWith("Logging step down", StringComparison.Ordinal));

    [Fact]
    public void StaleStepDownCallback_DoesNotLowerLevel()
    {
        var time = new FakeTimeProvider();
        using var controller = new StepUpLoggingController(AutoOptions(), null, time);

        controller.Trigger();
        var firstGen = controller.CurrentGeneration;
        Assert.True(controller.IsSteppedUp);

        // Advance past the rate-limit window and extend the window (bumps the generation).
        time.Advance(TimeSpan.FromSeconds(6));
        controller.Trigger();
        var extendedGen = controller.CurrentGeneration;
        Assert.NotEqual(firstGen, extendedGen);

        // A stale callback carrying the old generation must be ignored.
        controller.InvokeStepDownForTest(firstGen);

        Assert.True(controller.IsSteppedUp);
        Assert.Equal(LogEventLevel.Information, controller.LevelSwitch.MinimumLevel);
    }

    [Fact]
    public void StaleStepDown_ThenGenuineStepDown_RestoresBase()
    {
        var time = new FakeTimeProvider();
        var output = new CollectingSink();
        using var controller = new StepUpLoggingController(AutoOptions(), new LoggerConfiguration().WriteTo.Sink(output).CreateLogger(), time);

        controller.Trigger();
        var firstGen = controller.CurrentGeneration;

        time.Advance(TimeSpan.FromSeconds(6));
        controller.Trigger();
        var currentGen = controller.CurrentGeneration;

        // Stale callback ignored.
        controller.InvokeStepDownForTest(firstGen);
        Assert.True(controller.IsSteppedUp);

        // Genuine callback (current generation) steps down exactly once.
        controller.InvokeStepDownForTest(currentGen);
        Assert.False(controller.IsSteppedUp);
        Assert.Equal(LogEventLevel.Warning, controller.LevelSwitch.MinimumLevel);
        Assert.Equal(1, StepDowns(output));

        // A second genuine-looking callback after step-down must be a no-op (guarded by the stepped-up flag),
        // so the active-state counter can never go negative.
        controller.InvokeStepDownForTest(controller.CurrentGeneration);
        Assert.False(controller.IsSteppedUp);
        Assert.Equal(LogEventLevel.Warning, controller.LevelSwitch.MinimumLevel);
        Assert.Equal(1, StepDowns(output));
    }

    [Fact]
    public void RateLimit_UsesMonotonicClock_NotWallClock()
    {
        var time = new FakeTimeProvider();
        using var controller = new StepUpLoggingController(AutoOptions(), null, time);

        controller.Trigger();
        var gen1 = controller.CurrentGeneration;

        // Within the 5s monotonic window -> rate-limited, no extend, generation unchanged.
        time.Advance(TimeSpan.FromSeconds(2));
        controller.Trigger();
        Assert.Equal(gen1, controller.CurrentGeneration);

        // Past the 5s monotonic window -> extend, generation advances.
        time.Advance(TimeSpan.FromSeconds(4));
        controller.Trigger();
        Assert.NotEqual(gen1, controller.CurrentGeneration);
    }

    [Fact]
    public void GenuineStepDown_RestoresBaseLevel()
    {
        var time = new FakeTimeProvider();
        using var controller = new StepUpLoggingController(AutoOptions(), null, time);

        controller.Trigger();
        Assert.True(controller.IsSteppedUp);

        controller.InvokeStepDownForTest(controller.CurrentGeneration);

        Assert.False(controller.IsSteppedUp);
        Assert.Equal(LogEventLevel.Warning, controller.LevelSwitch.MinimumLevel);
    }

    [Fact]
    public void StepDownCallback_WhenNotSteppedUp_IsNoOp()
    {
        var time = new FakeTimeProvider();
        var output = new CollectingSink();
        using var controller = new StepUpLoggingController(AutoOptions(), new LoggerConfiguration().WriteTo.Sink(output).CreateLogger(), time);

        Assert.False(controller.IsSteppedUp);

        controller.InvokeStepDownForTest(controller.CurrentGeneration);

        Assert.False(controller.IsSteppedUp);
        Assert.Equal(LogEventLevel.Warning, controller.LevelSwitch.MinimumLevel);
        Assert.Equal(0, StepDowns(output));
    }
}
