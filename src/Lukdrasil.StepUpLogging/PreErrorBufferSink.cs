using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// In-memory per-context ring buffer that captures recent log events and flushes them
/// to an inner logger when an Error or Fatal event is observed. Context is keyed by
/// OpenTelemetry/Activity <c>TraceId</c> when available; otherwise a global buffer is used.
/// Implements proper disposal to prevent memory leaks in LRU cache.
/// Instruments buffer flush operations with ActivitySource for distributed tracing.
/// </summary>
internal sealed class PreErrorBufferSink(ILogger bypassLogger, int capacityPerContext, int maxContexts) : ILogEventSink, IDisposable
{
    private readonly ILogger _bypassLogger = bypassLogger ?? throw new ArgumentNullException(nameof(bypassLogger));
    private readonly int _capacityPerContext = Math.Max(1, capacityPerContext);
    private readonly int _maxContexts = Math.Max(1, maxContexts);

    private readonly ConcurrentDictionary<string, Buffer> _buffers = new();
    private readonly object _lruGate = new();
    private readonly LinkedList<string> _lru = new();
    private bool _disposed;

    private static readonly ActivitySource BufferActivitySource = new("Lukdrasil.StepUpLogging.Buffer", "1.0.0");

    private static readonly Meter Meter = new("StepUpLogging.Buffer", "1.0.0");
    private static readonly Counter<long> BufferedEventsCounter = Meter.CreateCounter<long>("buffer_events_total", unit: "count", description: "Total number of events buffered");
    private static readonly Counter<long> FlushedEventsCounter = Meter.CreateCounter<long>("buffer_flushed_events_total", unit: "count", description: "Total number of events flushed due to error");
    private static readonly Counter<long> FlushCounter = Meter.CreateCounter<long>("buffer_flush_total", unit: "count", description: "Number of buffer flush operations");
    private static readonly Counter<long> EvictedContextsCounter = Meter.CreateCounter<long>("buffer_evicted_contexts_total", unit: "count", description: "Number of evicted contexts due to LRU");

    private sealed class Buffer
    {
        private readonly Queue<LogEvent> _queue;
        private readonly int _capacity;
        private readonly object _gate = new();

        public DateTime LastTouchedUtc { get; private set; }

        public Buffer(int capacity)
        {
            _capacity = Math.Max(1, capacity);
            _queue = new Queue<LogEvent>(_capacity);
            LastTouchedUtc = DateTime.UtcNow;
        }

        public void Enqueue(LogEvent evt)
        {
            lock (_gate)
            {
                if (_queue.Count == _capacity)
                {
                    _queue.Dequeue();
                }
                _queue.Enqueue(evt);
                LastTouchedUtc = DateTime.UtcNow;
            }
        }

        public int FlushTo(ILogger logger)
        {
            LogEvent[] items;
            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    return 0;
                }
                items = _queue.ToArray();
                _queue.Clear();
                LastTouchedUtc = DateTime.UtcNow;
            }

            // Write outside lock - with tracing
            using (BufferActivitySource.StartActivity("FlushBufferedEvents", ActivityKind.Internal))
            {
                foreach (var e in items)
                {
                    logger.Write(e);
                }
            }

            return items.Length;
        }
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent is null || _disposed)
        {
            return;
        }

        var key = GetContextKey(logEvent);
        var buffer = GetOrCreateBuffer(key);

        if (logEvent.Level >= LogEventLevel.Error)
        {
            // Flush buffered events first (do not re-emit the triggering error to avoid duplication)
            var flushed = buffer.FlushTo(_bypassLogger);
            if (flushed > 0)
            {
                FlushCounter.Add(1);
                FlushedEventsCounter.Add(flushed);
            }

            // No buffering of the error event itself
            return;
        }

        buffer.Enqueue(logEvent);
        BufferedEventsCounter.Add(1);
    }

    private Buffer GetOrCreateBuffer(string key)
    {
        var buffer = _buffers.GetOrAdd(key, _ =>
        {
            var b = new Buffer(_capacityPerContext);
            TrackLruFor(key);
            return b;
        });

        TrackLruFor(key);
        return buffer;
    }

    private void TrackLruFor(string key)
    {
        lock (_lruGate)
        {
            // Move key to front
            var node = _lru.Find(key);
            if (node is not null)
            {
                _lru.Remove(node);
            }
            _lru.AddFirst(key);

            // Enforce max contexts, evicting oldest first
            while (_lru.Count > _maxContexts)
            {
                var last = _lru.Last;
                if (last is not null)
                {
                    _lru.RemoveLast();
                    if (_buffers.TryRemove(last.Value, out _))
                    {
                        EvictedContextsCounter.Add(1);
                    }
                }
            }
        }
    }

    private static string GetContextKey(LogEvent evt)
    {
        // Prefer Activity TraceId
        var activity = Activity.Current;
        if (activity is not null && activity.IdFormat == ActivityIdFormat.W3C)
        {
            return activity.TraceId.ToString();
        }

        // Fallback to TraceId property if present (from Serilog.Enrichers.OpenTelemetry)
        if (evt.Properties.TryGetValue("TraceId", out var traceIdValue) && traceIdValue is ScalarValue sv && sv.Value is string s && !string.IsNullOrWhiteSpace(s))
        {
            return s;
        }

        // Global buffer key when no context available
        return "__global__";
    }

    /// <summary>
    /// Flushes all remaining buffered events and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Best effort flush all remaining buffers on dispose
        lock (_lruGate)
        {
            foreach (var kvp in _buffers.ToArray())
            {
                kvp.Value.FlushTo(_bypassLogger);
            }
            _buffers.Clear();
            _lru.Clear();
        }
    }
}
