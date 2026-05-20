using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Serilog sink that gates log events by the current <see cref="LoggingLevelSwitch"/> and
/// suppresses events already routed to bypass sinks (marked <c>IsRequestSummary</c> or
/// <c>IsImmediate</c>) to guarantee exactly-once delivery.
/// </summary>
internal sealed class StepUpSink : ILogEventSink, IDisposable
{
    private readonly Serilog.ILogger _innerLogger;
    private readonly LoggingLevelSwitch _levelSwitch;
    private bool _disposed;

    /// <param name="innerLogger">Pre-configured output logger (OTLP / Console / File sinks). No enrichers needed — events arrive already enriched from the root pipeline.</param>
    /// <param name="levelSwitch">Shared switch managed by <see cref="StepUpLoggingController"/>.</param>
    public StepUpSink(Serilog.ILogger innerLogger, LoggingLevelSwitch levelSwitch)
    {
        _innerLogger = innerLogger ?? throw new ArgumentNullException(nameof(innerLogger));
        _levelSwitch = levelSwitch ?? throw new ArgumentNullException(nameof(levelSwitch));
    }

    public void Emit(LogEvent logEvent)
    {
        if (_disposed || logEvent is null) return;

        // Gate by step-up level switch
        if (logEvent.Level < _levelSwitch.MinimumLevel) return;

        // Drop bypass-routed markers to prevent duplication with SummarySink / ImmediateSink
        if (IsBoolTrue(logEvent, LogProperties.IsRequestSummary)) return;
        if (IsBoolTrue(logEvent, LogProperties.IsImmediate)) return;

        _innerLogger.Write(logEvent);
    }

    private static bool IsBoolTrue(LogEvent evt, string propertyName) =>
        evt.Properties.TryGetValue(propertyName, out var val)
        && val is ScalarValue sv
        && sv.Value is bool b && b;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_innerLogger is IDisposable d) d.Dispose();
    }
}
