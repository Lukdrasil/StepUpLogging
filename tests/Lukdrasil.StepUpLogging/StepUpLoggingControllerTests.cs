using System;
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

            controller.EmitRequestSummary("GET", "/api/values", 200, 123.45, "abcdef");

            Assert.NotNull(sink.LastEvent);
            Assert.Equal("RequestSummary {Method} {Path} {StatusCode} {ElapsedMs}", sink.LastEvent!.MessageTemplate.Text);
            Assert.True(sink.LastEvent.Properties.ContainsKey("IsRequestSummary"));
            // Check some properties
            Assert.Equal("GET", ((ScalarValue)sink.LastEvent.Properties["Method"]).Value);
            Assert.Equal("/api/values", ((ScalarValue)sink.LastEvent.Properties["Path"]).Value);
            Assert.Equal(200L, ((ScalarValue)sink.LastEvent.Properties["StatusCode"]).Value);
        }
    }
}
