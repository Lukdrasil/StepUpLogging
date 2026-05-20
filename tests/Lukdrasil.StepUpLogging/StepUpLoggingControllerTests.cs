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
    }
}
