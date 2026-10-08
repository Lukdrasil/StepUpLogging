using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Lukdrasil.StepUpLogging.Benchmarks;

/// <summary>What the logging and audit benchmarks share: events, requests, a host, and the checks that keep a scenario honest.</summary>
internal static class BenchmarkFixtures
{
    /// <summary>The category the benchmark events come from: under <c>MyApp</c>, away from every library category.</summary>
    internal const string OrderHandlerContext = "MyApp.Orders.OrderHandler";

    /// <summary>The message template of every benchmark event.</summary>
    internal const string HandledTemplate = "Handled {Item} {Token}";

    internal const string RequestPath = "/api/orders/8f3a2c1e/items/42/token/xyz9";

    private const string BenchmarkAssemblyName = "Lukdrasil.StepUpLogging.Benchmarks";

    internal static readonly string[] PatternSources =
    [
        @"token/[^/]+",
        @"password=[^&]+",
        @"api[_-]?key=[^&]+",
        @"Bearer\s+[A-Za-z0-9._-]+",
        @"\b\d{16}\b",
    ];

    /// <summary>Counts what reaches an output sink behind the step-up switch, or the bypass logger: the host's config-declared <c>Counting</c> sink.</summary>
    internal static readonly NullLogEventSink ExportedEvents = new();

    internal static readonly ILogEventPropertyFactory PropertyFactory = new ScalarPropertyFactory();

    private static readonly MessageTemplate Template = new MessageTemplateParser().Parse(HandledTemplate);

    /// <summary>The first <paramref name="count"/> redaction patterns, compiled the way the host compiles them.</summary>
    internal static CompiledRedactionPatterns SamplePatterns(int count = 5) =>
        new(PatternSources.Take(count).Select(StepUpLoggingExtensions.CompilePattern).ToArray());

    internal static LogEvent Event(LogEventLevel level, string sourceContext, params LogEventProperty[] properties) =>
        new(
            DateTimeOffset.UtcNow,
            level,
            exception: null,
            Template,
            [new LogEventProperty("SourceContext", new ScalarValue(sourceContext)), .. properties]);

    internal static LogEventProperty Text(string name, string value) => new(name, new ScalarValue(value));

    /// <summary>Ordinary string properties that no redaction pattern matches.</summary>
    internal static LogEventProperty[] TextProperties(int count) =>
        [.. Enumerable.Range(0, count).Select(i => Text($"Field{i}", $"value {i} of an ordinary log message"))];

    /// <summary>An event holding the shape of a log call: the two template arguments plus <paramref name="extra"/> string properties.</summary>
    internal static LogEvent HandledEvent(LogEventLevel level, int extra) =>
        Event(level, OrderHandlerContext, [Text("Item", "42"), Text("Token", "an ordinary value"), .. TextProperties(extra)]);

    /// <summary>Starts a W3C <see cref="Activity"/> and leaves it as <see cref="Activity.Current"/>; the caller stops it.</summary>
    internal static Activity StartTrace()
    {
        var activity = new Activity("benchmark-trace");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        return activity.Start();
    }

    internal static void StopTrace(Activity? activity)
    {
        activity?.Stop();
        activity?.Dispose();
    }

    /// <summary>A request with route values, a query string, a client address, a user agent, a forwarded-for header and <paramref name="headerCount"/> more headers.</summary>
    internal static DefaultHttpContext Request(int headerCount)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = RequestPath;
        context.Request.QueryString = new QueryString("?page=2&sort=asc");
        context.Request.RouteValues = new RouteValueDictionary { ["orderId"] = "8f3a2c1e", ["itemId"] = "42", ["token"] = "xyz9" };
        context.Request.Headers.UserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.7, 10.0.0.1";
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        foreach (var index in Enumerable.Range(0, headerCount))
        {
            context.Request.Headers[$"X-Custom-{index}"] = $"custom value {index}";
        }

        return context;
    }

    /// <summary>The audit event's <c>Data</c>: <paramref name="entries"/> string entries, or none at all for zero.</summary>
    internal static IReadOnlyDictionary<string, object?>? AuditData(int entries) =>
        entries == 0
            ? null
            : Enumerable.Range(0, entries).ToDictionary(i => $"key{i}", i => (object?)$"value {i}");

    /// <summary>
    /// A host wired by <c>AddStepUpLogging()</c> with no OTLP, console or file output; the only output
    /// sink is the <c>Counting</c> sink, so the benchmarks measure the pipeline and not an exporter.
    /// <paramref name="configure"/> is the consumer's Serilog hook; it runs on the Verbose root.
    /// </summary>
    internal static IHost BuildHost(
        IEnumerable<KeyValuePair<string, string?>> settings,
        Action<IServiceProvider, LoggerConfiguration>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SerilogStepUp:EnableOtlpExporter"] = "false",
            ["Serilog:Using:0"] = BenchmarkAssemblyName,
            ["Serilog:WriteTo:0:Name"] = nameof(BenchmarkSinkExtensions.Counting),
        });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddStepUpLogging(configureOptions: null, configure: configure);
        return builder.Build();
    }

    /// <summary>Settings that give the host the first <paramref name="count"/> sample redaction patterns and turn property redaction on.</summary>
    internal static IEnumerable<KeyValuePair<string, string?>> RedactionSettings(int count = 5) =>
        PatternSources.Take(count)
            .Select((pattern, index) => StepUpSetting($"RedactionRegexes:{index}", pattern))
            .Append(StepUpSetting("RedactLogEventProperties", "true"));

    /// <summary>One <c>SerilogStepUp</c> option as a configuration entry; <paramref name="key"/> is the option's path under the section.</summary>
    internal static KeyValuePair<string, string?> StepUpSetting(string key, string value) =>
        KeyValuePair.Create<string, string?>($"SerilogStepUp:{key}", value);

    /// <summary>How many events <paramref name="action"/> sent to an output sink.</summary>
    internal static int ExportedBy(Action action)
    {
        var before = ExportedEvents.Received;
        action();
        return ExportedEvents.Received - before;
    }

    /// <summary>How much <paramref name="instrumentName"/> of <paramref name="meterName"/> counted while <paramref name="action"/> ran.</summary>
    internal static long CountedBy(string meterName, string instrumentName, Action action)
    {
        long total = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, subscriber) =>
        {
            if (instrument.Meter.Name == meterName && instrument.Name == instrumentName)
            {
                subscriber.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => total += value);
        listener.Start();
        action();
        return total;
    }

    /// <summary>Throws when a benchmark's scenario is not the one its name promises, so no number is measured over the wrong thing.</summary>
    internal static void Require(bool scenarioHolds, string scenario)
    {
        if (!scenarioHolds)
        {
            throw new InvalidOperationException($"the benchmark scenario does not do what its name says: {scenario}");
        }
    }

    private sealed class ScalarPropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false) =>
            new(name, new ScalarValue(value));
    }
}

/// <summary>A sink that drops everything and counts what it was given.</summary>
internal sealed class NullLogEventSink : ILogEventSink
{
    private int _received;

    internal int Received => Volatile.Read(ref _received);

    public void Emit(LogEvent logEvent) => Interlocked.Increment(ref _received);
}

/// <summary>Stands in for the audit store: remembers the last record and says it was stored.</summary>
internal sealed class NullAuditSink : IAuditEventSink
{
    internal AuditEvent? Last { get; private set; }

    public ValueTask<AuditWriteResult> WriteAsync(AuditEvent auditEvent)
    {
        Last = auditEvent;
        return ValueTask.FromResult(AuditWriteResult.Stored);
    }
}

/// <summary>Lets the host's Serilog configuration attach <see cref="BenchmarkFixtures.ExportedEvents"/> as <c>Serilog:WriteTo</c> sink <c>Counting</c>.</summary>
public static class BenchmarkSinkExtensions
{
    /// <summary>Adds the benchmarks' counting sink.</summary>
    public static Serilog.LoggerConfiguration Counting(this Serilog.Configuration.LoggerSinkConfiguration sinkConfiguration) =>
        sinkConfiguration.Sink(BenchmarkFixtures.ExportedEvents);
}
