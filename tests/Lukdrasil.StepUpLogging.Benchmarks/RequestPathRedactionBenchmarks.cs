using BenchmarkDotNet.Attributes;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Lukdrasil.StepUpLogging.Benchmarks;

[BenchmarkCategory("Logging")]
public class RequestPathRedactionBenchmarks
{
    private const string Path = "/api/orders/8f3a2c1e/items/42/token/xyz9";

    private CompiledRedactionPatterns _patterns = null!;
    private Dictionary<string, object?> _routeValues = null!;
    private ILogEventEnricher _pathEnricher = null!;
    private LogEvent _event = null!;
    private LogEventProperty _rawRequestPath = null!;

    [GlobalSetup]
    public void Setup()
    {
        _patterns = BenchmarkFixtures.SamplePatterns();
        _routeValues = new Dictionary<string, object?>
        {
            ["orderId"] = "8f3a2c1e",
            ["itemId"] = "42",
            ["token"] = "xyz9",
        };
        var enricherType = typeof(CompiledRedactionPatterns).Assembly.GetType("Lukdrasil.StepUpLogging.PathPropertyRedactionEnricher")
            ?? throw new InvalidOperationException("Lukdrasil.StepUpLogging declares no PathPropertyRedactionEnricher");
        _pathEnricher = (ILogEventEnricher)Activator.CreateInstance(
            enricherType, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            null, [_patterns], null)!;
        _rawRequestPath = new LogEventProperty("RequestPath", new ScalarValue(Path));
        _event = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Debug, null, new MessageTemplateParser().Parse("Handled {Item}"),
        [
            new LogEventProperty("SourceContext", new ScalarValue("MyApp.Orders.OrderHandler")),
            _rawRequestPath,
            new LogEventProperty("RequestId", new ScalarValue("0HN7ABCDEF:00000001")),
            new LogEventProperty("ConnectionId", new ScalarValue("0HN7ABCDEF")),
            new LogEventProperty("TraceId", new ScalarValue("4bf92f3577b34da6a3ce929d0e0e4736")),
            new LogEventProperty("SpanId", new ScalarValue("00f067aa0ba902b7")),
            new LogEventProperty("Application", new ScalarValue("Orders")),
            new LogEventProperty("Item", new ScalarValue(42)),
        ]);
    }

    [Benchmark]
    public Dictionary<string, object?> RedactPathAndRouteValues() =>
        RequestPathRedaction.RedactRouteValues(_routeValues, Path, _patterns.Redact(Path), _patterns);

    [Benchmark]
    public LogEvent EnrichRequestPath()
    {
        _event.AddOrUpdateProperty(_rawRequestPath);
        _pathEnricher.Enrich(_event, BenchmarkFixtures.PropertyFactory);
        return _event;
    }
}
