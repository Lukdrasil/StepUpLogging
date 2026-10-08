using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Serilog sink that exports an event at or above <c>max(switch, pin unless diagnostic, floor)</c>: the current
/// <see cref="LoggingLevelSwitch"/> level; the base level for <c>SourceContext</c> categories in the NeverStepUp
/// deny-list, skipped while diagnostic is active; and the <see cref="CategoryFloorMap"/> floor of the category.
/// Suppresses events already routed to bypass sinks (marked <c>IsRequestSummary</c> or
/// <c>IsImmediate</c>) to guarantee exactly-once delivery. Events it rejects are handed to the
/// <see cref="PreErrorBufferSink"/>, when one is configured, as held-back events.
/// </summary>
internal sealed class StepUpSink : ILogEventSink, IDisposable
{
    private readonly Serilog.ILogger _innerLogger;
    private readonly LoggingLevelSwitch _levelSwitch;
    private readonly LogEventLevel _baseLevel;
    private readonly string[] _neverStepUpCategories;
    private readonly PreErrorBufferSink? _heldBackBuffer;
    private readonly CategoryFloorMap? _categoryFloors;
    private readonly Func<bool>? _isDiagnosticActive;
    private bool _disposed;

    /// <param name="innerLogger">Pre-configured output logger (OTLP / Console / File sinks). No enrichers needed — events arrive already enriched from the root pipeline.</param>
    /// <param name="levelSwitch">Shared switch managed by <see cref="StepUpLoggingController"/>.</param>
    /// <param name="baseLevel">The level the step-up raises from; listed categories are pinned to it.</param>
    /// <param name="neverStepUpCategories"><c>SourceContext</c> prefixes never raised above <paramref name="baseLevel"/> (already blank-filtered at the wiring site).</param>
    /// <param name="heldBackBuffer">Buffer that receives events this sink does not export; <see langword="null"/> when pre-error buffering is off.</param>
    /// <param name="categoryFloors">Per-category floors; <see langword="null"/> when none are configured.</param>
    /// <param name="isDiagnosticActive">Controller-owned diagnostic flag; <see langword="null"/> reads as never active.</param>
    public StepUpSink(Serilog.ILogger innerLogger, LoggingLevelSwitch levelSwitch, LogEventLevel baseLevel, string[] neverStepUpCategories, PreErrorBufferSink? heldBackBuffer = null, CategoryFloorMap? categoryFloors = null, Func<bool>? isDiagnosticActive = null)
    {
        _innerLogger = innerLogger ?? throw new ArgumentNullException(nameof(innerLogger));
        _levelSwitch = levelSwitch ?? throw new ArgumentNullException(nameof(levelSwitch));
        _baseLevel = baseLevel;
        _neverStepUpCategories = neverStepUpCategories ?? throw new ArgumentNullException(nameof(neverStepUpCategories));
        _heldBackBuffer = heldBackBuffer;
        _categoryFloors = categoryFloors;
        _isDiagnosticActive = isDiagnosticActive;
    }

    public void Emit(LogEvent logEvent)
    {
        if (Refuses(logEvent)) return;

        if (logEvent.Level < ExportMinimum(logEvent))
        {
            _heldBackBuffer?.Hold(logEvent);
            return;
        }

        _innerLogger.Write(logEvent);
    }

    /// <summary>
    /// Whether the event is not this sink's to export: it arrives after dispose, is null, was dropped by the
    /// enrichment gate (no enricher ran on it, ADR 0026 D3), or is routed to a bypass sink (marked
    /// <c>IsRequestSummary</c> or <c>IsImmediate</c>), which prevents duplication with SummarySink / ImmediateSink.
    /// </summary>
    private bool Refuses(LogEvent? logEvent)
        => _disposed
           || logEvent is null
           || EnrichmentGate.TakeSkipped(logEvent)
           || LogProperties.HasFlag(logEvent, LogProperties.IsRequestSummary)
           || LogProperties.HasFlag(logEvent, LogProperties.IsImmediate);

    /// <summary>
    /// The level an event must reach to be exported: <c>max(switch, NeverStepUp pin unless diagnostic, category
    /// floor)</c>. The pin keeps listed categories at BaseLevel, a floor only raises the minimum, neither adds verbosity.
    /// </summary>
    private LogEventLevel ExportMinimum(LogEvent logEvent)
    {
        var switchLevel = _levelSwitch.MinimumLevel;
        if (!HasCategoryRules() || !CategoryPrefix.TryGetSourceContext(logEvent, out var source)) return switchLevel;

        var diagnosticActive = IsDiagnosticActive();
        return Higher(PinnedMinimum(source, diagnosticActive, switchLevel), FloorFor(source, diagnosticActive));
    }

    private bool HasCategoryRules() => _neverStepUpCategories.Length > 0 || _categoryFloors is not null;

    private LogEventLevel PinnedMinimum(string source, bool diagnosticActive, LogEventLevel switchLevel)
        => !diagnosticActive && CategoryPrefix.MatchesAny(source, _neverStepUpCategories)
            ? Higher(_baseLevel, switchLevel)
            : switchLevel;

    private LogEventLevel FloorFor(string source, bool diagnosticActive)
        => _categoryFloors is not null && _categoryFloors.TryGetFloor(source, diagnosticActive, out var floor)
            ? floor
            : LogEventLevel.Verbose;

    private bool IsDiagnosticActive() => _isDiagnosticActive?.Invoke() ?? false;

    private static LogEventLevel Higher(LogEventLevel left, LogEventLevel right) => left > right ? left : right;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_innerLogger is IDisposable d) d.Dispose();
    }
}
