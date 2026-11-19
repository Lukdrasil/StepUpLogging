using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Keeps every log entry a component writes, so a test can assert on the level and the formatted
/// message of the one it cares about, and the trace it was written in.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    // Locked on every side: the drain worker logs from its own thread while the test reads.
    private readonly List<(LogLevel Level, string Message, ActivityTraceId TraceId)> _entries = [];

    public bool IsEnabled(LogLevel logLevel) => true;

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add((logLevel, formatter(state, exception), Activity.Current?.TraceId ?? default));
        }
    }

    public IReadOnlyList<string> MessagesAt(LogLevel level)
    {
        lock (_entries)
        {
            return [.. _entries.Where(entry => entry.Level == level).Select(entry => entry.Message)];
        }
    }

    /// <summary>The TraceId of the activity current when each entry at <paramref name="level"/> was written, in the order of <see cref="MessagesAt"/>.</summary>
    public IReadOnlyList<ActivityTraceId> TraceIdsAt(LogLevel level)
    {
        lock (_entries)
        {
            return [.. _entries.Where(entry => entry.Level == level).Select(entry => entry.TraceId)];
        }
    }
}
