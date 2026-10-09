using System.Collections.Concurrent;
using System.Diagnostics;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Sink wiring, keyed events and thread helpers shared by the Prebuffer* tests of <see cref="PreErrorBufferSink"/>.
/// </summary>
internal static class PrebufferHarness
{
    private const int MaxStripeCandidates = 100_000;

    private static readonly MessageTemplateParser Parser = new();

    /// <summary>The trace id the ring tests hold their numbered events on.</summary>
    public const string RingTrace = "ring-trace";

    /// <summary>A bypass logger that writes every flushed event into <paramref name="collector"/>.</summary>
    public static ILogger BypassInto(ILogEventSink collector)
        => new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(collector).CreateLogger();

    /// <summary>An Information event with the message <paramref name="text"/> on the trace <paramref name="traceId"/>.</summary>
    public static LogEvent Held(string traceId, string text)
        => new(DateTimeOffset.UtcNow, LogEventLevel.Information, null, Parser.Parse(text), [TraceIdProperty(traceId)]);

    /// <summary>An Error event on the trace <paramref name="traceId"/>; emitting it flushes that trace's buffer.</summary>
    public static LogEvent Error(string traceId)
        => new(DateTimeOffset.UtcNow, LogEventLevel.Error, null, Parser.Parse("err"), [TraceIdProperty(traceId)]);

    /// <summary>The message texts of <paramref name="events"/>, in order.</summary>
    public static string[] Texts(IEnumerable<LogEvent> events)
        => events.Select(e => e.MessageTemplate.Text).ToArray();

    /// <summary>The <c>TraceId</c> property value of <paramref name="logEvent"/>.</summary>
    public static string? TraceOf(LogEvent logEvent)
        => logEvent.Properties.TryGetValue("TraceId", out var value) && value is ScalarValue { Value: string traceId } ? traceId : null;

    /// <summary>The first <paramref name="count"/> generated trace ids that <paramref name="sink"/> assigns to <paramref name="stripe"/>.</summary>
    public static List<string> TracesInStripe(PreErrorBufferSink sink, int stripe, int count)
    {
        var traces = new List<string>(count);
        for (var candidate = 0; traces.Count < count && candidate < MaxStripeCandidates; candidate++)
        {
            var traceId = $"stripe{stripe}-trace{candidate}";
            if (sink.StripeOf(traceId) == stripe)
            {
                traces.Add(traceId);
            }
        }

        Assert.Equal(count, traces.Count);
        return traces;
    }

    /// <summary>Holds <paramref name="held"/> numbered events on <see cref="RingTrace"/>, emits an Error, and returns the flushed texts.</summary>
    public static string[] HoldThenFlush(int capacity, int held)
    {
        Activity.Current = null;
        var collector = new Collector();
        using var sink = new PreErrorBufferSink(BypassInto(collector), capacity, maxContexts: 16, minimumLevel: LogEventLevel.Information);

        HoldNumbered(sink, 1, held);
        sink.Emit(Error(RingTrace));

        return Texts(collector.Events);
    }

    /// <summary>Holds the events <c>e{first}</c> to <c>e{last}</c> on <see cref="RingTrace"/>.</summary>
    public static void HoldNumbered(PreErrorBufferSink sink, int first, int last)
    {
        for (var i = first; i <= last; i++)
        {
            sink.Hold(Held(RingTrace, $"e{i}"));
        }
    }

    /// <summary>Starts <paramref name="body"/> on a background thread, so a test that fails cannot keep the run alive.</summary>
    public static Thread StartBackground(Action body)
    {
        var thread = new Thread(() => body()) { IsBackground = true };
        thread.Start();
        return thread;
    }

    private static LogEventProperty TraceIdProperty(string traceId) => new("TraceId", new ScalarValue(traceId));

    /// <summary>Thread-safe sink that records every event the bypass logger writes.</summary>
    internal sealed class Collector : ILogEventSink
    {
        /// <summary>The recorded events, in write order.</summary>
        public ConcurrentQueue<LogEvent> Events { get; } = new();

        /// <inheritdoc />
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }
}
