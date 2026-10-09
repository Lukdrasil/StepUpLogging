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
/// Traces are tracked in up to 16 independent LRU stripes, so a hold locks only its own stripe, and a trace's ring grows
/// on demand up to <paramref name="capacityPerContext"/> events.
/// Implements proper disposal to prevent memory leaks in LRU cache.
///
/// Optionally instruments buffer flush operations with ActivitySource for distributed tracing.
/// To enable tracing, register StepUpLoggingExtensions.BufferActivitySourceName in your OpenTelemetry config.
/// </summary>
internal sealed class PreErrorBufferSink(ILogger bypassLogger, int capacityPerContext, int maxContexts, LogEventLevel minimumLevel) : ILogEventSink, IDisposable
{
    private readonly ILogger _bypassLogger = bypassLogger ?? throw new ArgumentNullException(nameof(bypassLogger));
    private readonly int _capacityPerContext = Math.Max(1, capacityPerContext);

    private const int MinTracesPerStripe = 64;
    private const int MaxStripes = 16;
    private const int InitialSlots = 4;
    private const int MidSlots = 16;
    private const int StripeTailChars = 4;
    private const uint FnvOffsetBasis = 2166136261u;
    private const uint FnvPrime = 16777619u;
    private const uint Fmix32C1 = 0x85EBCA6Bu;
    private const uint Fmix32C2 = 0xC2B2AE35u;

    private readonly ConcurrentDictionary<string, Buffer> _buffers = new();
    private readonly TraceLruStripe[] _stripes = CreateStripes(Math.Max(1, maxContexts));
    private volatile bool _disposed;

    /// <summary>Number of live per-context buffers. Exposed for tests.</summary>
    internal int ContextCount => _buffers.Count;

    /// <summary>Number of LRU stripes for <paramref name="maxContexts"/> traces. Exposed for tests.</summary>
    internal static int StripeCountFor(int maxContexts) => Math.Clamp(maxContexts / MinTracesPerStripe, 1, MaxStripes);

    /// <summary>Capacity of each of <paramref name="stripes"/> stripes, summing to <paramref name="maxContexts"/>. Exposed for tests.</summary>
    internal static int[] StripeCapacities(int maxContexts, int stripes)
    {
        var share = maxContexts / stripes;
        var remainder = maxContexts % stripes;
        return [.. Enumerable.Range(0, stripes).Select(i => i < remainder ? share + 1 : share)];
    }

    /// <summary>
    /// Index of the stripe that owns <paramref name="key"/>, derived from its last 4 characters and the same in every process.
    /// Exposed for tests.
    /// </summary>
    internal int StripeOf(string key)
    {
        if (_stripes.Length == 1)
        {
            return 0;
        }

        var hash = HashTail(key.AsSpan(Math.Max(0, key.Length - StripeTailChars)));
        return (int)(((ulong)hash * (uint)_stripes.Length) >> 32);
    }

    private static uint HashTail(ReadOnlySpan<char> tail)
    {
        var hash = FnvOffsetBasis;
        foreach (var c in tail)
        {
            hash = (hash ^ c) * FnvPrime;
        }

        return Fmix32(hash);
    }

    private static uint Fmix32(uint hash)
    {
        hash ^= hash >> 16;
        hash *= Fmix32C1;
        hash ^= hash >> 13;
        hash *= Fmix32C2;
        return hash ^ (hash >> 16);
    }

    private static TraceLruStripe[] CreateStripes(int maxContexts)
        => Array.ConvertAll(StripeCapacities(maxContexts, StripeCountFor(maxContexts)), capacity => new TraceLruStripe(capacity));

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

    /// <summary>
    /// One trace's ring of held events. It starts with <see cref="InitialSlots"/> slots and doubles up to its capacity as
    /// events arrive, then overwrites its oldest event.
    /// </summary>
    private sealed class Buffer
    {
        private readonly int _capacity;
        private readonly object _gate = new();
        private LogEvent[] _items;
        private int _head;
        private int _count;

        public Buffer(int capacity)
        {
            _capacity = Math.Max(1, capacity);
            _items = new LogEvent[Math.Min(InitialSlots, _capacity)];
        }

        public void Enqueue(LogEvent evt)
        {
            lock (_gate)
            {
                if (_count == _capacity)
                {
                    _items[_head] = evt;
                    _head = (_head + 1) % _items.Length;
                    return;
                }

                if (_count == _items.Length)
                {
                    Grow();
                }
                _items[(_head + _count) % _items.Length] = evt;
                _count++;
            }
        }

        public int FlushTo(ILogger logger)
        {
            LogEvent[] items;
            lock (_gate)
            {
                if (_count == 0)
                {
                    return 0;
                }
                items = TakeAll();
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

        private void Grow()
        {
            var grown = new LogEvent[Math.Min(_items.Length * 2, _capacity)];
            CopyInOrder(grown);
            _items = grown;
            _head = 0;
        }

        private void CopyInOrder(LogEvent[] target)
        {
            var untilWrap = Math.Min(_count, _items.Length - _head);
            Array.Copy(_items, _head, target, 0, untilWrap);
            Array.Copy(_items, 0, target, untilWrap, _count - untilWrap);
        }

        private LogEvent[] TakeAll()
        {
            var snapshot = new LogEvent[_count];
            CopyInOrder(snapshot);
            Array.Clear(_items);
            _head = 0;
            _count = 0;
            return snapshot;
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

    // Get-or-create, LRU touch, eviction and enqueue for a key must complete as one unit under the key's stripe lock;
    // otherwise a concurrent touch for another key of the same stripe could evict this key's buffer between the
    // touch and the enqueue, silently orphaning the event. Keys of different stripes never share a lock.
    private void BufferEvent(string key, LogEvent logEvent)
    {
        var stripe = _stripes[StripeOf(key)];
        lock (stripe.Gate)
        {
            if (_disposed)
            {
                return;
            }

            var buffer = _buffers.GetOrAdd(key, static (_, capacity) => new Buffer(capacity), _capacityPerContext);
            Evict(stripe.Touch(key));
            BeforeEnqueueTestHook?.Invoke();
            buffer.Enqueue(logEvent);
            AfterEnqueueTestHook?.Invoke();
        }
    }

    private void Evict(string? evictedKey)
    {
        if (evictedKey is not null && _buffers.TryRemove(evictedKey, out _))
        {
            EvictedContextsCounter.Add(1);
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

        foreach (var stripe in _stripes)
        {
            lock (stripe.Gate)
            {
                stripe.Clear();
            }
        }
        _buffers.Clear();
    }
}
