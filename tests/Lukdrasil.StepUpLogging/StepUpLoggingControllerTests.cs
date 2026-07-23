using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Serilog;
using Serilog.Events;
using Serilog.Core;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests
{
    public class StepUpLoggingControllerTests
    {
        private sealed class CaptureSink : ILogEventSink
        {
            public LogEvent? LastEvent { get; private set; }
            public void Emit(LogEvent logEvent)
            {
                LastEvent = logEvent;
            }
        }

        [Fact(DisplayName = "EmitRequestSummary_WritesSummaryToSummaryLogger")]
        public void EmitRequestSummary_WritesSummaryToSummaryLogger()
        {
            var opts = new StepUpLoggingOptions();
            var sink = new CaptureSink();
            var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

            var controller = new StepUpLoggingController(opts, logger);

            var routeParams = new Dictionary<string, object?> { ["id"] = "1", ["order"] = "abc" };
            controller.EmitRequestSummary("GET", "/api/values", 200, 123.45, "abcdef", "id=1&order=abc", routeParams);

            Assert.NotNull(sink.LastEvent);
            Assert.Equal("Request finished {Method} {Path} {StatusCode} {ElapsedMs}", sink.LastEvent!.MessageTemplate.Text);
            Assert.True(sink.LastEvent.Properties.ContainsKey("IsRequestSummary"));
            // Check some properties
            Assert.Equal("GET", ((ScalarValue)sink.LastEvent.Properties["Method"]).Value);
            Assert.Equal("/api/values", ((ScalarValue)sink.LastEvent.Properties["Path"]).Value);
            Assert.Equal(200, Convert.ToInt32(((ScalarValue)sink.LastEvent.Properties["StatusCode"]).Value));
            Assert.Equal("id=1&order=abc", ((ScalarValue)sink.LastEvent.Properties["QueryString"]).Value);
            Assert.True(sink.LastEvent.Properties.ContainsKey("RouteParameters"));
        }

        [Fact(DisplayName = "EmitRequestSummary_WithUserAgentAndClientIp_AddsProperties")]
        public void EmitRequestSummary_WithUserAgentAndClientIp_AddsProperties()
        {
            var opts = new StepUpLoggingOptions();
            var sink = new CaptureSink();
            var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
            var controller = new StepUpLoggingController(opts, logger);

            controller.EmitRequestSummary("GET", "/api/test", 200, 50.0, userAgent: "Mozilla/5.0", clientIp: "127.0.0.1");

            Assert.NotNull(sink.LastEvent);
            Assert.Equal("Mozilla/5.0", ((ScalarValue)sink.LastEvent!.Properties["UserAgent"]).Value);
            Assert.Equal("127.0.0.1", ((ScalarValue)sink.LastEvent.Properties["ClientIp"]).Value);
        }

        [Fact(DisplayName = "EmitRequestSummary_WithNullUserAgentAndClientIp_OmitsProperties")]
        public void EmitRequestSummary_WithNullUserAgentAndClientIp_OmitsProperties()
        {
            var opts = new StepUpLoggingOptions();
            var sink = new CaptureSink();
            var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
            var controller = new StepUpLoggingController(opts, logger);

            controller.EmitRequestSummary("GET", "/api/test", 200, 50.0, userAgent: null, clientIp: null);

            Assert.NotNull(sink.LastEvent);
            Assert.False(sink.LastEvent!.Properties.ContainsKey("UserAgent"));
            Assert.False(sink.LastEvent.Properties.ContainsKey("ClientIp"));
        }

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
            long ticks = Stopwatch.GetTimestamp();
            var opts = new StepUpLoggingOptions
            {
                Mode = StepUpMode.Auto,
                BaseLevel = "Information",
                StepUpLevel = "Information",
                DurationSeconds = 300
            };

            using var controller = new StepUpLoggingController(opts, null, () => ticks);

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
            long ticks = Stopwatch.GetTimestamp();
            var opts = new StepUpLoggingOptions
            {
                Mode = StepUpMode.Auto,
                BaseLevel = "Information",
                StepUpLevel = "Information",
                DurationSeconds = 60,
                MaxContinuousStepUpSeconds = 120,
                StepUpCooldownSeconds = 300
            };

            using var controller = new StepUpLoggingController(opts, null, () => ticks);

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
            listener.SetMeasurementEventCallback<int>((_, measurement, _, _) => netActive += measurement);
            listener.Start();

            controller.Trigger();

            // Extend once so the forced-cap trigger inherits this generation. The cap path does not
            // re-arm the timer, so this is the generation a queued callback would still carry.
            ticks += Seconds(60);
            controller.Trigger();
            long staleGeneration = controller.CurrentGeneration;

            ticks += Seconds(70);
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

        private static long Seconds(double s) => (long)(s * Stopwatch.Frequency);
    }
}
