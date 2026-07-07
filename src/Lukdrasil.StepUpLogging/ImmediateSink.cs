using System.Diagnostics.Metrics;
using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Serilog sink that forwards log events tagged with <c>IsImmediate=true</c> to an inner logger,
/// bypassing the step-up <see cref="LoggingLevelSwitch"/>. Events always export regardless of
/// whether step-up is currently active.
/// </summary>
internal sealed class ImmediateSink : ILogEventSink, IDisposable
{
    // Static so the meter is created once for the process, not per sink instance (which leaked an
    // undisposed Meter on every pipeline rebuild).
    private static readonly Meter Meter = new("StepUpLogging.Immediate", "1.0.0");
    private static readonly Counter<long> ProcessedCounter = Meter.CreateCounter<long>("immediate_processed_total", "count", "Number of immediate log events forwarded");

    private readonly Serilog.ILogger _target;
    private bool _disposed;

    public ImmediateSink(Serilog.ILogger target)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public void Emit(LogEvent logEvent)
    {
        if (_disposed || logEvent is null) return;

        try
        {
            if (LogProperties.HasFlag(logEvent, LogProperties.IsImmediate))
            {
                _target.Write(logEvent);
                ProcessedCounter.Add(1);
            }
        }
        catch
        {
            // swallow to avoid affecting logging pipeline
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
