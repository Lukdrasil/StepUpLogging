using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>Pins how <see cref="RequestSummaryWriter.Write"/> converts route values that are not strings.</summary>
public class RequestSummaryRouteValueTests
{
    private sealed class LastEventSink : ILogEventSink
    {
        public LogEvent? LastEvent { get; private set; }

        public void Emit(LogEvent logEvent) => LastEvent = logEvent;
    }

    [Fact]
    public void Write_NonStringRouteValues_AreConvertedLikeTheLogger()
    {
        var sink = new LastEventSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var writer = TestSummaries.Writer(logger);
        var routeParameters = new Dictionary<string, object?> { ["id"] = 42, ["slug"] = null, ["name"] = "orders" };

        writer.Write(TestSummaries.Inbound("GET", "/orders/42", 200, 1.5, routeParameters: routeParameters));

        var elements = Assert.IsType<DictionaryValue>(sink.LastEvent!.Properties["RouteParameters"]).Elements;
        Assert.Equal(
            ["\"id\": 42", "\"slug\": null", "\"name\": \"orders\""],
            elements.Select(element => $"{element.Key}: {element.Value}"));
    }
}
