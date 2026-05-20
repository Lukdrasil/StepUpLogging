using Serilog.Core;
using Serilog.Events;
using System.Diagnostics.Metrics;

namespace Lukdrasil.StepUpLogging;

internal sealed class SummarySink : ILogEventSink, IDisposable
{
    private readonly Serilog.ILogger _target;
    private readonly Counter<long> _processed;
    private bool _disposed;

    public SummarySink(Serilog.ILogger target)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        var meter = new Meter("StepUpLogging.RequestLogging", "1.0.0");
        _processed = meter.CreateCounter<long>("summary_processed_total", "count", "Number of request summary events processed");
    }

    public void Emit(LogEvent logEvent)
    {
        if (_disposed || logEvent is null) return;

        try
        {
            if (LogProperties.HasFlag(logEvent, LogProperties.IsRequestSummary))
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
        // nothing to dispose here - _target is DI-managed
    }
}
