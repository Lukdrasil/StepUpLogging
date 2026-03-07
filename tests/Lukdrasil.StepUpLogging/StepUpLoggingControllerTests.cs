using System;
using System.Collections.Generic;
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
    }
}
