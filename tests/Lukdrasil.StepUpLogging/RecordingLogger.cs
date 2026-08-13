using Microsoft.Extensions.Logging;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Keeps every log entry a component writes, so a test can assert on the level and the formatted
/// message of the one it cares about.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    // Locked on every side: the drain worker logs from its own thread while the test reads.
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public bool IsEnabled(LogLevel logLevel) => true;

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }

    public IReadOnlyList<string> MessagesAt(LogLevel level)
    {
        lock (_entries)
        {
            return [.. _entries.Where(entry => entry.Level == level).Select(entry => entry.Message)];
        }
    }
}
