using System.Diagnostics;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Tests;

public class StepUpControllerCapTests
{
    private static long Seconds(double s) => (long)(s * Stopwatch.Frequency);

    private static StepUpLoggingOptions CappedOptions(int cap = 120, int cooldown = 300, int duration = 60, StepUpMode mode = StepUpMode.Auto) => new()
    {
        Mode = mode,
        BaseLevel = "Warning",
        StepUpLevel = "Information",
        DurationSeconds = duration,
        MaxContinuousStepUpSeconds = cap,
        StepUpCooldownSeconds = cooldown
    };

    [Fact]
    public void CapDisabled_ContinuousTriggering_NeverForcesStepDown()
    {
        long ticks = Stopwatch.GetTimestamp();
        var opts = new StepUpLoggingOptions
        {
            Mode = StepUpMode.Auto,
            BaseLevel = "Warning",
            StepUpLevel = "Information",
            DurationSeconds = 300
        };
        using var controller = new StepUpLoggingController(opts, null, () => ticks);

        controller.Trigger();
        Assert.True(controller.IsSteppedUp);

        for (int i = 0; i < 100; i++)
        {
            ticks += Seconds(6);
            controller.Trigger();
            Assert.True(controller.IsSteppedUp);
        }
    }

    [Fact]
    public void CapEnabled_ContinuousTriggeringPastCap_ForcesStepDown()
    {
        long ticks = Stopwatch.GetTimestamp();
        using var controller = new StepUpLoggingController(CappedOptions(), null, () => ticks);

        controller.Trigger();
        Assert.True(controller.IsSteppedUp);

        for (int i = 0; i < 25; i++)
        {
            ticks += Seconds(6);
            controller.Trigger();
        }

        Assert.False(controller.IsSteppedUp);
        Assert.Equal(LogEventLevel.Warning, controller.LevelSwitch.MinimumLevel);
    }

    [Fact]
    public void DuringCooldown_TriggerDoesNotStepUp_AfterCooldownItDoes()
    {
        long ticks = Stopwatch.GetTimestamp();
        using var controller = new StepUpLoggingController(CappedOptions(), null, () => ticks);

        controller.Trigger();
        for (int i = 0; i < 25; i++)
        {
            ticks += Seconds(6);
            controller.Trigger();
        }
        Assert.False(controller.IsSteppedUp);
        Assert.True(controller.IsInCooldown);

        ticks += Seconds(10);
        controller.Trigger();
        Assert.False(controller.IsSteppedUp);

        ticks += Seconds(310);
        controller.Trigger();
        Assert.True(controller.IsSteppedUp);
        Assert.Equal(LogEventLevel.Information, controller.LevelSwitch.MinimumLevel);
    }

    [Fact]
    public void CapForcedStepDown_ThenStaleCallback_NoDoubleStepDown()
    {
        long ticks = Stopwatch.GetTimestamp();
        using var controller = new StepUpLoggingController(CappedOptions(), null, () => ticks);

        controller.Trigger();
        var staleGeneration = controller.CurrentGeneration;

        for (int i = 0; i < 25; i++)
        {
            ticks += Seconds(6);
            controller.Trigger();
        }
        Assert.False(controller.IsSteppedUp);

        controller.InvokeStepDownForTest(staleGeneration);

        Assert.False(controller.IsSteppedUp);
        Assert.Equal(LogEventLevel.Warning, controller.LevelSwitch.MinimumLevel);
    }

    [Fact]
    public void AlwaysOnMode_IgnoresCap()
    {
        long ticks = Stopwatch.GetTimestamp();
        using var controller = new StepUpLoggingController(CappedOptions(mode: StepUpMode.AlwaysOn), null, () => ticks);

        Assert.True(controller.IsSteppedUp);
        for (int i = 0; i < 50; i++)
        {
            ticks += Seconds(6);
            controller.Trigger();
        }
        Assert.True(controller.IsSteppedUp);
    }

    [Fact]
    public void DisabledMode_IgnoresCap()
    {
        long ticks = Stopwatch.GetTimestamp();
        using var controller = new StepUpLoggingController(CappedOptions(mode: StepUpMode.Disabled), null, () => ticks);

        Assert.False(controller.IsSteppedUp);
        for (int i = 0; i < 50; i++)
        {
            ticks += Seconds(6);
            controller.Trigger();
        }
        Assert.False(controller.IsSteppedUp);
    }
}
