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

    public static LoggerConfiguration StepUpAuditCapture(this LoggerAuditSinkConfiguration auditSinkConfiguration, string key)
        => auditSinkConfiguration.Sink(new Sink(Captured.GetOrAdd(key, _ => new ConcurrentQueue<LogEvent>())));

    public static LoggerConfiguration RootProbe(this LoggerFilterConfiguration filterConfiguration, string key)
    {
        var events = Captured.GetOrAdd(key, _ => new ConcurrentQueue<LogEvent>());
        return filterConfiguration.ByIncludingOnly(logEvent =>
        {
            events.Enqueue(Snapshot(logEvent));
            return true;
        });
    }

    public static int Count(string key, string token)
        => Captured.TryGetValue(key, out var events)
            ? events.Count(e => e.RenderMessage().Contains(token, StringComparison.Ordinal))
            : 0;

    public static LogEvent[] Events(string key)
        => Captured.TryGetValue(key, out var events) ? events.ToArray() : [];

    public static void Forget(string key) => Captured.TryRemove(key, out _);

    private static LogEvent Snapshot(LogEvent logEvent)
        => new(
            logEvent.Timestamp,
            logEvent.Level,
            logEvent.Exception,
            logEvent.MessageTemplate,
            logEvent.Properties.Select(p => new LogEventProperty(p.Key, p.Value)));

    private sealed class Sink(ConcurrentQueue<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Enqueue(logEvent);
    }
}
