using System.Diagnostics.Metrics;
using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Forwards the events that carry one marker flag to the bypass logger, so they export regardless of the step-up
/// <see cref="LoggingLevelSwitch"/>: request summaries (<c>IsRequestSummary</c>) and immediate events (<c>IsImmediate</c>).
/// </summary>
internal sealed class BypassSink : ILogEventSink, IDisposable
{
    // Static so each meter is created once for the process, not per sink instance (which leaked an
    // undisposed Meter on every pipeline rebuild).
    private static readonly Meter SummaryMeter = new("StepUpLogging.RequestLogging", "1.0.0");
    private static readonly Counter<long> SummaryCounter = SummaryMeter.CreateCounter<long>("summary_processed_total", "count", "Number of request summary events processed");
    private static readonly Meter ImmediateMeter = new("StepUpLogging.Immediate", "1.0.0");
    private static readonly Counter<long> ImmediateCounter = ImmediateMeter.CreateCounter<long>("immediate_processed_total", "count", "Number of immediate log events forwarded");

    private readonly string _flag;
    private readonly Serilog.ILogger _target;
    private readonly Counter<long> _forwarded;
    private bool _disposed;

    private BypassSink(string flag, Serilog.ILogger target, Counter<long> forwarded)
    {
        ArgumentNullException.ThrowIfNull(target);
        _flag = flag;
        _target = target;
        _forwarded = forwarded;
    }

    /// <summary>Forwards events tagged <c>IsRequestSummary=true</c> to <paramref name="target"/>.</summary>
    internal static BypassSink RequestSummaries(Serilog.ILogger target) => new(LogProperties.IsRequestSummary, target, SummaryCounter);

    /// <summary>Forwards events tagged <c>IsImmediate=true</c> to <paramref name="target"/>.</summary>
    internal static BypassSink Immediate(Serilog.ILogger target) => new(LogProperties.IsImmediate, target, ImmediateCounter);

    public void Emit(LogEvent logEvent)
    {
        if (_disposed || logEvent is null) return;

        try
        {
            if (LogProperties.HasFlag(logEvent, _flag))
            {
                _target.Write(logEvent);
                _forwarded.Add(1);
            }
        }
        catch
        {
            // swallow to avoid affecting logging pipeline
        }
    }

    /// <summary>Stops forwarding; the target is the bypass logger, owned by the controller.</summary>
    public void Dispose() => _disposed = true;
}
