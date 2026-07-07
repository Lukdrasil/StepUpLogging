using Serilog.Core;
using Serilog.Events;
using System.Diagnostics.Metrics;

namespace Lukdrasil.StepUpLogging;

internal sealed class SummarySink : ILogEventSink, IDisposable
{
    // Static so the meter is created once for the process, not per sink instance (which leaked an
    // undisposed Meter on every pipeline rebuild).
    private static readonly Meter Meter = new("StepUpLogging.RequestLogging", "1.0.0");
    private static readonly Counter<long> ProcessedCounter = Meter.CreateCounter<long>("summary_processed_total", "count", "Number of request summary events processed");

    private readonly Serilog.ILogger _target;
    private bool _disposed;

    public SummarySink(Serilog.ILogger target)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public void Emit(LogEvent logEvent)
    {
        if (_disposed || logEvent is null) return;

        try
        {
            if (LogProperties.HasFlag(logEvent, LogProperties.IsRequestSummary))
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
        // nothing to dispose here - _target is DI-managed
    }
}
