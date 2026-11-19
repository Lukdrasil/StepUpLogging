using Microsoft.Extensions.Time.Testing;
using Serilog;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Tests;

public class StepUpControllerCapTests
{

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
        var time = new FakeTimeProvider();
        var opts = new StepUpLoggingOptions
        {
            Mode = StepUpMode.Auto,
            BaseLevel = "Warning",
            StepUpLevel = "Information",
            DurationSeconds = 300
        };
        using var controller = new StepUpLoggingController(opts, null, time);

        controller.Trigger();
        Assert.True(controller.IsSteppedUp);

        for (int i = 0; i < 100; i++)
        {
            time.Advance(TimeSpan.FromSeconds(6));
            controller.Trigger();
            Assert.True(controller.IsSteppedUp);
        }
    }

    [Fact]
    public void CapEnabled_ContinuousTriggeringPastCap_ForcesStepDown()
    {
        var time = new FakeTimeProvider();
        using var controller = new StepUpLoggingController(CappedOptions(), null, time);

        controller.Trigger();
        Assert.True(controller.IsSteppedUp);

        for (int i = 0; i < 25; i++)
        {
            time.Advance(TimeSpan.FromSeconds(6));
            controller.Trigger();
        }

        Assert.False(controller.IsSteppedUp);
        Assert.Equal(LogEventLevel.Warning, controller.LevelSwitch.MinimumLevel);
    }

    [Fact]
    public void DuringCooldown_TriggerDoesNotStepUp_AfterCooldownItDoes()
    {
        var time = new FakeTimeProvider();
        using var controller = new StepUpLoggingController(CappedOptions(), null, time);

        controller.Trigger();
        for (int i = 0; i < 25; i++)
        {
            time.Advance(TimeSpan.FromSeconds(6));
            controller.Trigger();
        }
        Assert.False(controller.IsSteppedUp);

        time.Advance(TimeSpan.FromSeconds(10));
        controller.Trigger();
        Assert.False(controller.IsSteppedUp);

        time.Advance(TimeSpan.FromSeconds(310));
        controller.Trigger();
        Assert.True(controller.IsSteppedUp);
        Assert.Equal(LogEventLevel.Information, controller.LevelSwitch.MinimumLevel);
    }

    [Fact]
    public void CapForcedStepDown_ThenStaleCallback_NoDoubleStepDown()
    {
        var time = new FakeTimeProvider();
        var output = new CollectingSink();
        using var controller = new StepUpLoggingController(CappedOptions(), new LoggerConfiguration().WriteTo.Sink(output).CreateLogger(), time);

        controller.Trigger();

        // The forced step-down does not advance the generation, so a callback armed by the last extension still
        // carries the current generation and passes the generation check; only the stepped-up guard stops it.
        long generationBeforeCap;
        do
        {
            generationBeforeCap = controller.CurrentGeneration;
            time.Advance(TimeSpan.FromSeconds(6));
            controller.Trigger();
        }
        while (controller.IsSteppedUp);
        Assert.Equal(generationBeforeCap, controller.CurrentGeneration);

        controller.InvokeStepDownForTest(generationBeforeCap);

        Assert.False(controller.IsSteppedUp);
        Assert.Equal(LogEventLevel.Warning, controller.LevelSwitch.MinimumLevel);
        Assert.Single(output.Events, e => e.MessageTemplate.Text.StartsWith("Logging step down", StringComparison.Ordinal));
    }

    [Fact]
    public void AlwaysOnMode_IgnoresCap()
    {
        var time = new FakeTimeProvider();
        using var controller = new StepUpLoggingController(CappedOptions(mode: StepUpMode.AlwaysOn), null, time);

        Assert.True(controller.IsSteppedUp);
        for (int i = 0; i < 50; i++)
        {
            time.Advance(TimeSpan.FromSeconds(6));
            controller.Trigger();
        }
        Assert.True(controller.IsSteppedUp);
    }

    [Fact]
    public void DisabledMode_IgnoresCap()
    {
        var time = new FakeTimeProvider();
        using var controller = new StepUpLoggingController(CappedOptions(mode: StepUpMode.Disabled), null, time);

        Assert.False(controller.IsSteppedUp);
        for (int i = 0; i < 50; i++)
        {
            time.Advance(TimeSpan.FromSeconds(6));
            controller.Trigger();
        }
        Assert.False(controller.IsSteppedUp);
    }
}
