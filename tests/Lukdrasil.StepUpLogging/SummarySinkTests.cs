using System;
using Serilog;
using Serilog.Events;
using Serilog.Parsing;
using Serilog.Core;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests
{
    public class SummarySinkTests
    {
        private sealed class CaptureSink : ILogEventSink
        {
            public LogEvent? LastEvent { get; private set; }
            public void Emit(LogEvent logEvent)
            {
                LastEvent = logEvent;
            }
        }

        [Fact(DisplayName = "SummarySink_ForwardsOnlyRequestSummaryEvents")]
        public void SummarySink_ForwardsOnlyRequestSummaryEvents()
        {
            var capture = new CaptureSink();
            var registered = new LoggerConfiguration().WriteTo.Sink(capture).CreateLogger();
            using var sink = new SummarySink(registered);

            var mt = new MessageTemplateParser().Parse("RequestSummary {Method} {Path} {StatusCode} {ElapsedMs}");
            var props = new[] {
                new LogEventProperty("IsRequestSummary", new ScalarValue(true)),
                new LogEventProperty("Method", new ScalarValue("GET")),
                new LogEventProperty("Path", new ScalarValue("/api/test")),
                new LogEventProperty("StatusCode", new ScalarValue(200)),
                new LogEventProperty("ElapsedMs", new ScalarValue(12.34))
            };

            var le = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, mt, props);
            sink.Emit(le);

            Assert.NotNull(capture.LastEvent);
            Assert.True(capture.LastEvent!.Properties.ContainsKey("IsRequestSummary"));

            // Now ensure non-summary doesn't forward
            var capture2 = new CaptureSink();
            var registered2 = new LoggerConfiguration().WriteTo.Sink(capture2).CreateLogger();
            using var sink2 = new SummarySink(registered2);

            var mt2 = new MessageTemplateParser().Parse("SomeOtherEvent {X}");
            var le2 = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, mt2, new LogEventProperty[0]);
            sink2.Emit(le2);

            Assert.Null(capture2.LastEvent);
        }
    }
}
