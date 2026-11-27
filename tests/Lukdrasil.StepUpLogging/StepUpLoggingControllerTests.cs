using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Time.Testing;
using Serilog.Events;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests
{
    public class StepUpLoggingControllerTests
    {
        [Fact(DisplayName = "BaseLevel_DefaultOptions_IsWarning")]
        public void BaseLevel_DefaultOptions_IsWarning()
        {
            var controller = new StepUpLoggingController(new StepUpLoggingOptions());

            Assert.Equal(LogEventLevel.Warning, controller.BaseLevel);
        }

        [Fact(DisplayName = "BaseLevel_ReflectsConfiguredBaseLevelString")]
        public void BaseLevel_ReflectsConfiguredBaseLevelString()
        {
            var controller = new StepUpLoggingController(new StepUpLoggingOptions { BaseLevel = "Error" });

            Assert.Equal(LogEventLevel.Error, controller.BaseLevel);
        }

        [Fact(DisplayName = "IsSteppedUp_AutoMode_BaseLevelEqualsStepUpLevel_TracksExplicitFlagNotLevelComparison")]
        public void IsSteppedUp_AutoMode_BaseLevelEqualsStepUpLevel_TracksExplicitFlagNotLevelComparison()
        {
            var time = new FakeTimeProvider();
            var opts = new StepUpLoggingOptions
            {
                Mode = StepUpMode.Auto,
                BaseLevel = "Information",
                StepUpLevel = "Information",
                DurationSeconds = 300
            };

            using var controller = new StepUpLoggingController(opts, null, time);

            // With BaseLevel == StepUpLevel, a level comparison can never distinguish stepped-up
            // from not; only an explicit flag can. Before any trigger, the switch already sits at
            // this shared level, so a level-comparison implementation would report true here.
            Assert.False(controller.IsSteppedUp);

            controller.Trigger();
            Assert.True(controller.IsSteppedUp);

            controller.InvokeStepDownForTest(controller.CurrentGeneration);
            Assert.False(controller.IsSteppedUp);
        }

        [Fact(DisplayName = "PerformStepDown_AutoMode_BaseEqualsStepUp_ForcedCapThenStaleCallback_DecrementsActiveCounterOnce")]
        public void PerformStepDown_AutoMode_BaseEqualsStepUp_ForcedCapThenStaleCallback_DecrementsActiveCounterOnce()
        {
            var time = new FakeTimeProvider();
            var opts = new StepUpLoggingOptions
            {
                Mode = StepUpMode.Auto,
                BaseLevel = "Information",
                StepUpLevel = "Information",
                DurationSeconds = 100,
                MaxContinuousStepUpSeconds = 120,
                StepUpCooldownSeconds = 300
            };

            using var controller = new StepUpLoggingController(opts, null, time);

            int netActive = 0;
            using var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == "StepUpLogging" && instrument.Name == "stepup_active")
                    {
                        l.EnableMeasurementEvents(instrument);
                    }
                }
            };
            // The fake clock fires on this thread, so only this controller's measurements are recorded here.
            var testThread = Environment.CurrentManagedThreadId;
            listener.SetMeasurementEventCallback<int>((_, measurement, _, _) =>
            {
                if (Environment.CurrentManagedThreadId == testThread) netActive += measurement;
            });
            listener.Start();

            controller.Trigger();

            // Extend once so the forced-cap trigger inherits this generation. The cap path does not
            // re-arm the timer, so this is the generation a queued callback would still carry.
            time.Advance(TimeSpan.FromSeconds(60));
            controller.Trigger();
            long staleGeneration = controller.CurrentGeneration;

            time.Advance(TimeSpan.FromSeconds(70));
            controller.Trigger();
            Assert.False(controller.IsSteppedUp);

            // The queued timer callback for the still-current generation races in after the forced
            // cap step-down. With BaseLevel == StepUpLevel the level-based guard cannot short-circuit
            // it (the switch reads the shared level either way), so only the explicit-flag guard keeps
            // this second call from double-decrementing the active-state counter.
            controller.InvokeStepDownForTest(staleGeneration);

            Assert.False(controller.IsSteppedUp);
            Assert.Equal(LogEventLevel.Information, controller.LevelSwitch.MinimumLevel);
            Assert.Equal(0, netActive);
        }
    }
}
