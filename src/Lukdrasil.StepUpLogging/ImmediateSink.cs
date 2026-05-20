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
    private readonly Serilog.ILogger _target;
    private readonly Counter<long> _processed;
    private bool _disposed;

    public ImmediateSink(Serilog.ILogger target)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        var meter = new Meter("StepUpLogging.Immediate", "1.0.0");
        _processed = meter.CreateCounter<long>("immediate_processed_total", "count", "Number of immediate log events forwarded");
    }

    public void Emit(LogEvent logEvent)
    {
        if (_disposed || logEvent is null) return;

        try
        {
            if (logEvent.Properties.TryGetValue(LogProperties.IsImmediate, out var val)
                && val is ScalarValue sv && sv.Value is bool b && b)
            {
                _target.Write(logEvent);
                _processed.Add(1);
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
