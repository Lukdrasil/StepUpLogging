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
/// In-memory per-context ring buffer of held-back events: events <see cref="StepUpSink"/> did not
/// export, passed to <see cref="Hold"/> at or above <paramref name="minimumLevel"/>. They are
/// flushed to an inner logger when an Error or Fatal event is observed and dropped on dispose.
/// Events below <paramref name="minimumLevel"/> are dropped before buffering. Context is keyed by OpenTelemetry/Activity <c>TraceId</c> when available;
/// otherwise a global buffer is used.
/// Implements proper disposal to prevent memory leaks in LRU cache.
///
/// Optionally instruments buffer flush operations with ActivitySource for distributed tracing.
/// To enable tracing, register StepUpLoggingExtensions.BufferActivitySourceName in your OpenTelemetry config.
/// </summary>
internal sealed class PreErrorBufferSink(ILogger bypassLogger, int capacityPerContext, int maxContexts, LogEventLevel minimumLevel) : ILogEventSink, IDisposable
{
    private readonly ILogger _bypassLogger = bypassLogger ?? throw new ArgumentNullException(nameof(bypassLogger));
    private readonly int _capacityPerContext = Math.Max(1, capacityPerContext);
    private readonly int _maxContexts = Math.Max(1, maxContexts);

    private readonly ConcurrentDictionary<string, Buffer> _buffers = new();
    private readonly object _lruGate = new();
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _lruNodes = new();
    private bool _disposed;

    /// <summary>Number of live per-context buffers. Exposed for tests.</summary>
    internal int ContextCount => _buffers.Count;

    /// <summary>Number of LRU stripes for <paramref name="maxContexts"/> traces. Exposed for tests.</summary>
    internal static int StripeCountFor(int maxContexts) => 0; // af-stub

    /// <summary>Capacity of each of <paramref name="stripes"/> stripes, summing to <paramref name="maxContexts"/>. Exposed for tests.</summary>
    internal static int[] StripeCapacities(int maxContexts, int stripes) => new int[stripes]; // af-stub

    /// <summary>Index of the stripe that owns <paramref name="key"/>. Exposed for tests.</summary>
    internal int StripeOf(string key) => (int)((uint)key.GetHashCode() % 2u); // af-stub

    /// <summary>
    /// Test-only seam invoked between the LRU touch and the enqueue of a buffered event, so a
    /// test can simulate a concurrent evictor landing in that window. No-op in production.
    /// </summary>
    internal Action? BeforeEnqueueTestHook { get; set; }

    /// <summary>
    /// Test-only seam invoked immediately after the enqueue, still inside the same buffering
    /// operation, so a test can deterministically observe the just-buffered state before any
    /// concurrent eviction can land. No-op in production.
    /// </summary>
    internal Action? AfterEnqueueTestHook { get; set; }

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

            // Create activity span only if there are actual events to flush
            using (StepUpLoggingExtensions.BufferActivitySource.StartActivity("FlushBufferedEvents", ActivityKind.Internal))
            {
                foreach (var e in items)
                {
                    logger.Write(e);
                }
            }

            return items.Length;
        }
    }

    /// <summary>
    /// Flushes the context's buffer to the bypass logger on an Error or Fatal event. Other events are ignored;
    /// held-back events arrive through <see cref="Hold"/>.
    /// </summary>
    public void Emit(LogEvent logEvent)
    {
        if (logEvent is null || _disposed || logEvent.Level < LogEventLevel.Error)
        {
            return;
        }

        // Flush only an EXISTING buffer; never allocate a buffer (or an LRU slot) for
        // an error in a context we have never buffered — there is nothing to flush.
        if (_buffers.TryGetValue(GetContextKey(logEvent), out var existing))
        {
            var flushed = existing.FlushTo(_bypassLogger);
            if (flushed > 0)
            {
                FlushCounter.Add(1);
                FlushedEventsCounter.Add(flushed);
            }
        }
    }

    /// <summary>
    /// Buffers an event <see cref="StepUpSink"/> did not export, under its trace key. Events below the minimum level,
    /// Error/Fatal events and bypass-marked events are ignored.
    /// </summary>
    internal void Hold(LogEvent logEvent)
    {
        if (logEvent is null || _disposed || logEvent.Level >= LogEventLevel.Error || logEvent.Level < minimumLevel)
        {
            return;
        }

        // ImmediateSink and SummarySink already forward these events — buffering them would cause a double-emit on flush
        if (LogProperties.HasFlag(logEvent, LogProperties.IsImmediate) || LogProperties.HasFlag(logEvent, LogProperties.IsRequestSummary))
            return;

        BufferEvent(GetContextKey(logEvent), logEvent);
        BufferedEventsCounter.Add(1);
    }

    // ponytail: get-or-create, LRU touch, and enqueue for a key must complete as one unit —
    // otherwise a concurrent TouchLru for a different key can evict this key's buffer between
    // the touch and the enqueue, silently orphaning the event. Holding _lruGate for the whole
    // operation serializes all buffering across every context behind one lock; revisit with
    // per-key locking if that ever shows up as a throughput bottleneck.
    private void BufferEvent(string key, LogEvent logEvent)
    {
        lock (_lruGate)
        {
            if (_disposed)
            {
                return;
            }

            var buffer = _buffers.GetOrAdd(key, static (_, capacity) => new Buffer(capacity), _capacityPerContext);
            TouchLru(key);
            BeforeEnqueueTestHook?.Invoke();
            buffer.Enqueue(logEvent);
            AfterEnqueueTestHook?.Invoke();
        }
    }

    // Must be called while holding _lruGate.
    private void TouchLru(string key)
    {
        // O(1) move-to-front via the node index.
        if (_lruNodes.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
        }
        else
        {
            _lruNodes[key] = _lru.AddFirst(key);
        }

        // Enforce max contexts, evicting least-recently-touched first.
        while (_lru.Count > _maxContexts)
        {
            var last = _lru.Last;
            if (last is not null)
            {
                _lru.RemoveLast();
                _lruNodes.Remove(last.Value);
                if (_buffers.TryRemove(last.Value, out _))
                {
                    EvictedContextsCounter.Add(1);
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
    /// Drops all remaining held-back events without export and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_lruGate)
        {
            _buffers.Clear();
            _lru.Clear();
            _lruNodes.Clear();
        }
    }
}
