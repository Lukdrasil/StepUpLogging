using Microsoft.Extensions.Logging;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Keeps every log entry a component writes, so a test can assert on the level and the formatted
/// message of the one it cares about.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public bool IsEnabled(LogLevel logLevel) => true;

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    public IEnumerable<string> MessagesAt(LogLevel level)
    {
        lock (Entries)
        {
            return Entries.Where(entry => entry.Level == level).Select(entry => entry.Message).ToList();
        }
    }
}
