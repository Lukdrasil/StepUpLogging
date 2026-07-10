using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Serilog sink that gates log events by the current <see cref="LoggingLevelSwitch"/>, except
/// for <c>SourceContext</c> categories in the deny-list which are pinned to the base level and never stepped up.
/// Suppresses events already routed to bypass sinks (marked <c>IsRequestSummary</c> or
/// <c>IsImmediate</c>) to guarantee exactly-once delivery.
/// </summary>
internal sealed class StepUpSink : ILogEventSink, IDisposable
{
    private const string SourceContextPropertyName = "SourceContext";

    private readonly Serilog.ILogger _innerLogger;
    private readonly LoggingLevelSwitch _levelSwitch;
    private readonly LogEventLevel _baseLevel;
    private readonly string[] _neverStepUpCategories;
    private bool _disposed;

    /// <param name="innerLogger">Pre-configured output logger (OTLP / Console / File sinks). No enrichers needed — events arrive already enriched from the root pipeline.</param>
    /// <param name="levelSwitch">Shared switch managed by <see cref="StepUpLoggingController"/>.</param>
    /// <param name="baseLevel">The level the step-up raises from; listed categories are pinned to it.</param>
    /// <param name="neverStepUpCategories"><c>SourceContext</c> prefixes never raised above <paramref name="baseLevel"/> (already blank-filtered at the wiring site).</param>
    public StepUpSink(Serilog.ILogger innerLogger, LoggingLevelSwitch levelSwitch, LogEventLevel baseLevel, string[] neverStepUpCategories)
    {
        _innerLogger = innerLogger ?? throw new ArgumentNullException(nameof(innerLogger));
        _levelSwitch = levelSwitch ?? throw new ArgumentNullException(nameof(levelSwitch));
        _baseLevel = baseLevel;
        _neverStepUpCategories = neverStepUpCategories ?? throw new ArgumentNullException(nameof(neverStepUpCategories));
    }

    public void Emit(LogEvent logEvent)
    {
        if (_disposed || logEvent is null) return;

        // Gate by step-up level switch; listed categories are pinned to BaseLevel so the
        // step-up never raises them (the max keeps the deny-list from ever adding verbosity).
        var minimum = IsNeverStepUp(logEvent)
            ? (LogEventLevel)Math.Max((int)_baseLevel, (int)_levelSwitch.MinimumLevel)
            : _levelSwitch.MinimumLevel;
        if (logEvent.Level < minimum) return;

        // Drop bypass-routed markers to prevent duplication with SummarySink / ImmediateSink
        if (IsBoolTrue(logEvent, LogProperties.IsRequestSummary)) return;
        if (IsBoolTrue(logEvent, LogProperties.IsImmediate)) return;

        _innerLogger.Write(logEvent);
    }

    private bool IsNeverStepUp(LogEvent evt)
    {
        if (_neverStepUpCategories.Length == 0) return false;
        if (!evt.Properties.TryGetValue(SourceContextPropertyName, out var value)
            || value is not ScalarValue { Value: string source }) return false;

        foreach (var prefix in _neverStepUpCategories)
        {
            if (source.Equals(prefix, StringComparison.Ordinal)) return true;
            if (source.Length > prefix.Length
                && source[prefix.Length] == '.'
                && source.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        return false;
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
