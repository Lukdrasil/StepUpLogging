using System.Collections.Concurrent;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Tests;

public static class KeyedCaptureSink
{
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<LogEvent>> Captured = new();

    public static LoggerConfiguration StepUpCapture(this LoggerSinkConfiguration sinkConfiguration, string key)
        => sinkConfiguration.Sink(new Sink(Captured.GetOrAdd(key, _ => new ConcurrentQueue<LogEvent>())));

    public static int Count(string key, string token)
        => Captured.TryGetValue(key, out var events)
            ? events.Count(e => e.RenderMessage().Contains(token, StringComparison.Ordinal))
            : 0;

    public static LogEvent[] Events(string key)
        => Captured.TryGetValue(key, out var events) ? events.ToArray() : [];

    public static void Forget(string key) => Captured.TryRemove(key, out _);

    private sealed class Sink(ConcurrentQueue<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Enqueue(logEvent);
    }
}
