using BenchmarkDotNet.Attributes;
using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Benchmarks;

/// <summary>
/// What redacting a request's path and route values costs, and what <c>PathPropertyRedactionEnricher</c>
/// costs on an event that carries the raw request path.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Logging")]
public class RequestPathRedactionBenchmarks
{
    private CompiledRedactionPatterns _patterns = null!;
    private Dictionary<string, object?> _routeValues = null!;
    private ILogEventEnricher _pathEnricher = null!;
    private LogEvent _event = null!;
    private LogEventProperty _rawRequestPath = null!;

    /// <summary>Builds the patterns, the route values, the enricher and an event that carries the raw request path.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _patterns = BenchmarkFixtures.SamplePatterns();
        _routeValues = BenchmarkFixtures.Request(headerCount: 0).Request.RouteValues.ToDictionary(route => route.Key, route => route.Value);
        var enricherType = typeof(CompiledRedactionPatterns).Assembly.GetType("Lukdrasil.StepUpLogging.PathPropertyRedactionEnricher")
            ?? throw new InvalidOperationException("Lukdrasil.StepUpLogging declares no PathPropertyRedactionEnricher");
        _pathEnricher = (ILogEventEnricher)Activator.CreateInstance(
            enricherType, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            null, [_patterns], null)!;
        _rawRequestPath = BenchmarkFixtures.Text("RequestPath", BenchmarkFixtures.RequestPath);
        _event = BenchmarkFixtures.Event(
            LogEventLevel.Debug,
            BenchmarkFixtures.OrderHandlerContext,
            _rawRequestPath,
            BenchmarkFixtures.Text("RequestId", "0HN7ABCDEF:00000001"),
            BenchmarkFixtures.Text("ConnectionId", "0HN7ABCDEF"),
            BenchmarkFixtures.Text("TraceId", "4bf92f3577b34da6a3ce929d0e0e4736"),
            BenchmarkFixtures.Text("SpanId", "00f067aa0ba902b7"),
            BenchmarkFixtures.Text("Application", "Orders"),
            new LogEventProperty("Item", new ScalarValue(42)));
    }

    /// <summary>Redacts the route values of the request the way the middleware does, given the redacted path.</summary>
    [Benchmark]
    public Dictionary<string, object?> RedactPathAndRouteValues() =>
        RequestPathRedaction.RedactRouteValues(_routeValues, BenchmarkFixtures.RequestPath, _patterns.Redact(BenchmarkFixtures.RequestPath), _patterns);

    /// <summary>Runs the path enricher over an event that carries the raw request path.</summary>
    [Benchmark]
    public LogEvent EnrichRequestPath()
    {
        _event.AddOrUpdateProperty(_rawRequestPath);
        _pathEnricher.Enrich(_event, BenchmarkFixtures.PropertyFactory);
        return _event;
    }
}
