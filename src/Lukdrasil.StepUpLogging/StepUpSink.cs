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
        if (_disposed || logEvent is null) return;

        // Drop bypass-routed markers to prevent duplication with SummarySink / ImmediateSink
        if (IsBoolTrue(logEvent, LogProperties.IsRequestSummary)) return;
        if (IsBoolTrue(logEvent, LogProperties.IsImmediate)) return;

        // Export at max(switch, NeverStepUp pin unless diagnostic, category floor): the pin keeps
        // listed categories at BaseLevel, a floor only raises the minimum, neither adds verbosity.
        var diagnosticActive = _isDiagnosticActive?.Invoke() ?? false;
        var source = string.Empty;
        var hasSource = (_neverStepUpCategories.Length > 0 || _categoryFloors is not null)
            && CategoryPrefix.TryGetSourceContext(logEvent, out source);
        var minimum = hasSource && !diagnosticActive && CategoryPrefix.MatchesAny(source, _neverStepUpCategories)
            ? (LogEventLevel)Math.Max((int)_baseLevel, (int)_levelSwitch.MinimumLevel)
            : _levelSwitch.MinimumLevel;
        if (hasSource
            && _categoryFloors is not null
            && _categoryFloors.TryGetFloor(source, diagnosticActive, out var floor)
            && floor > minimum)
        {
            minimum = floor;
        }
        if (logEvent.Level < minimum)
        {
            _heldBackBuffer?.Hold(logEvent);
            return;
        }

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
