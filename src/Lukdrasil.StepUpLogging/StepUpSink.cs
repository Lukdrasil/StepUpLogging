using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Serilog sink that exports an event at or above the <see cref="CategoryVerdict.ExportMinimum"/> the
/// <see cref="CategoryPolicy"/> resolves from the current <see cref="LoggingLevelSwitch"/> level.
/// Suppresses events already routed to bypass sinks (marked <c>IsRequestSummary</c> or
/// <c>IsImmediate</c>) to guarantee exactly-once delivery. Events it rejects are handed to the
/// <see cref="PreErrorBufferSink"/>, when one is configured, as held-back events, unless the verdict keeps them out of it.
/// </summary>
internal sealed class StepUpSink : ILogEventSink, IDisposable
{
    private readonly Serilog.ILogger _innerLogger;
    private readonly LoggingLevelSwitch _levelSwitch;
    private readonly CategoryPolicy _policy;
    private readonly PreErrorBufferSink? _heldBackBuffer;
    private readonly Func<bool>? _isDiagnosticActive;
    private bool _disposed;

    /// <param name="innerLogger">Pre-configured output logger (OTLP / Console / File sinks). No enrichers needed — events arrive already enriched from the root pipeline.</param>
    /// <param name="levelSwitch">Shared switch managed by <see cref="StepUpLoggingController"/>.</param>
    /// <param name="policy">The category options that raise the export minimum or keep an event out of the buffer.</param>
    /// <param name="heldBackBuffer">Buffer that receives events this sink does not export; <see langword="null"/> when pre-error buffering is off.</param>
    /// <param name="isDiagnosticActive">Controller-owned diagnostic flag; <see langword="null"/> reads as never active.</param>
    public StepUpSink(Serilog.ILogger innerLogger, LoggingLevelSwitch levelSwitch, CategoryPolicy policy, PreErrorBufferSink? heldBackBuffer = null, Func<bool>? isDiagnosticActive = null)
    {
        _innerLogger = innerLogger ?? throw new ArgumentNullException(nameof(innerLogger));
        _levelSwitch = levelSwitch ?? throw new ArgumentNullException(nameof(levelSwitch));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _heldBackBuffer = heldBackBuffer;
        _isDiagnosticActive = isDiagnosticActive;
    }

    public void Emit(LogEvent logEvent)
    {
        if (Refuses(logEvent)) return;

        var verdict = _policy.Resolve(logEvent, _levelSwitch.MinimumLevel, _isDiagnosticActive);
        if (!verdict.Exports(logEvent.Level))
        {
            if (!verdict.KeptOutOfBuffer) _heldBackBuffer?.Hold(logEvent);
            return;
        }

        _innerLogger.Write(logEvent);
    }

    /// <summary>
    /// Whether the event is not this sink's to export: it arrives after dispose, is null, was dropped by the
    /// enrichment gate (no enricher ran on it, ADR 0026 D3), or is routed to a bypass sink (marked
    /// <c>IsRequestSummary</c> or <c>IsImmediate</c>), which prevents duplication with the two <see cref="BypassSink"/> routes.
    /// </summary>
    private bool Refuses(LogEvent? logEvent)
        => _disposed
           || logEvent is null
           || EnrichmentGate.TakeSkipped(logEvent)
           || LogProperties.HasFlag(logEvent, LogProperties.IsRequestSummary)
           || LogProperties.HasFlag(logEvent, LogProperties.IsImmediate);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_innerLogger is IDisposable d) d.Dispose();
    }
}
