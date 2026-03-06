using Serilog;
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
        if (_disposed || logEvent == null) return;

        try
        {
            if (logEvent.Properties.TryGetValue("IsRequestSummary", out var val))
            {
                if (val is ScalarValue sv && sv.Value is bool b && b)
                {
                    // Forward to configured summary logger which is responsible for exporting independently of LevelSwitch
                    _target.Write(logEvent);
                    _processed.Add(1);
                }
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
