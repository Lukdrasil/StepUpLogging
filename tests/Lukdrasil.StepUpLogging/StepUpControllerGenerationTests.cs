using System;
using System.Diagnostics;
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

    [Fact]
    public void StaleStepDownCallback_DoesNotLowerLevel()
    {
        long ticks = Stopwatch.GetTimestamp();
        using var controller = new StepUpLoggingController(AutoOptions(), null, () => ticks);

        controller.Trigger();
        var firstGen = controller.CurrentGeneration;
        Assert.True(controller.IsSteppedUp);

        // Advance past the rate-limit window and extend the window (bumps the generation).
        ticks += (long)(6 * Stopwatch.Frequency);
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
        long ticks = Stopwatch.GetTimestamp();
        using var controller = new StepUpLoggingController(AutoOptions(), null, () => ticks);

        controller.Trigger();
        var firstGen = controller.CurrentGeneration;

        ticks += (long)(6 * Stopwatch.Frequency);
        controller.Trigger();
        var currentGen = controller.CurrentGeneration;

        // Stale callback ignored.
        controller.InvokeStepDownForTest(firstGen);
        Assert.True(controller.IsSteppedUp);

        // Genuine callback (current generation) steps down exactly once.
        controller.InvokeStepDownForTest(currentGen);
        Assert.False(controller.IsSteppedUp);
        Assert.Equal(LogEventLevel.Warning, controller.LevelSwitch.MinimumLevel);

        // A second genuine-looking callback after step-down must be a no-op (guarded by level check),
        // so the active-state counter can never go negative.
        controller.InvokeStepDownForTest(controller.CurrentGeneration);
        Assert.False(controller.IsSteppedUp);
        Assert.Equal(LogEventLevel.Warning, controller.LevelSwitch.MinimumLevel);
    }

    [Fact]
    public void RateLimit_UsesMonotonicClock_NotWallClock()
    {
        long ticks = Stopwatch.GetTimestamp();
        using var controller = new StepUpLoggingController(AutoOptions(), null, () => ticks);

        controller.Trigger();
        var gen1 = controller.CurrentGeneration;

        // Within the 5s monotonic window -> rate-limited, no extend, generation unchanged.
        ticks += (long)(2 * Stopwatch.Frequency);
        controller.Trigger();
        Assert.Equal(gen1, controller.CurrentGeneration);

        // Past the 5s monotonic window -> extend, generation advances.
        ticks += (long)(4 * Stopwatch.Frequency);
        controller.Trigger();
        Assert.NotEqual(gen1, controller.CurrentGeneration);
    }

    [Fact]
    public void GenuineStepDown_RestoresBaseLevel()
    {
        long ticks = Stopwatch.GetTimestamp();
        using var controller = new StepUpLoggingController(AutoOptions(), null, () => ticks);

        controller.Trigger();
        Assert.True(controller.IsSteppedUp);

        controller.InvokeStepDownForTest(controller.CurrentGeneration);

        Assert.False(controller.IsSteppedUp);
        Assert.Equal(LogEventLevel.Warning, controller.LevelSwitch.MinimumLevel);
    }

    [Fact]
    public void StepDownCallback_WhenNotSteppedUp_IsNoOp()
    {
        long ticks = Stopwatch.GetTimestamp();
        using var controller = new StepUpLoggingController(AutoOptions(), null, () => ticks);

        Assert.False(controller.IsSteppedUp);

        controller.InvokeStepDownForTest(controller.CurrentGeneration);

        Assert.False(controller.IsSteppedUp);
        Assert.Equal(LogEventLevel.Warning, controller.LevelSwitch.MinimumLevel);
    }
}
