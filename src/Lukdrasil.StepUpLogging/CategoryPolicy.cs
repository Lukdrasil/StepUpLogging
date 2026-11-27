using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// What the category options decide about one event: the level it must reach to be exported, and whether it stays
/// out of the pre-error buffer when it is held back.
/// </summary>
internal readonly record struct CategoryVerdict(LogEventLevel ExportMinimum, bool KeptOutOfBuffer);

/// <summary>
/// The one runtime reader of every category option: <see cref="StepUpLoggingOptions.NeverStepUpCategories"/> (ADR 0021),
/// <see cref="StepUpLoggingOptions.CategoryFloors"/> with <see cref="StepUpLoggingOptions.DiagnosticExemptCategories"/>
/// (ADR 0024), <see cref="StepUpLoggingOptions.PreErrorBufferExcludeCategories"/> (ADR 0028), the framework defaults
/// (ADR 0029), <see cref="StepUpLoggingOptions.NeverTriggerCategories"/> and
/// <see cref="StepUpLoggingOptions.AlwaysExportCategories"/>. It drops blank prefixes, clears the pin and the
/// framework defaults in AlwaysOn mode, and matches every prefix by <see cref="CategoryPrefix"/>; callers read its
/// answers and match nothing themselves.
/// </summary>
internal sealed class CategoryPolicy
{
    private readonly LogEventLevel _baseLevel;
    private readonly string[] _neverStepUp;
    private readonly KeyValuePair<string, LogEventLevel>[] _floors;
    private readonly FrameworkCategoryRule[] _frameworkDefaults;
    private readonly string[] _diagnosticExempt;
    private readonly string[] _bufferExcluded;
    private readonly string[] _neverTrigger;
    private readonly string[] _alwaysExport;
    private readonly bool _hasExportRules;

    internal CategoryPolicy(StepUpLoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var alwaysOn = options.Mode == StepUpMode.AlwaysOn;
        _baseLevel = StepUpSettings.Level(options.BaseLevel);
        _neverStepUp = alwaysOn ? [] : StepUpSettings.NonBlank(options.NeverStepUpCategories);
        _floors = [.. (options.CategoryFloors ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f.Key))
            .Select(f => KeyValuePair.Create(f.Key, StepUpSettings.Level(f.Value)))
            .OrderByDescending(f => f.Key.Length)];
        _frameworkDefaults = alwaysOn ? [] : [.. StepUpSettings.FrameworkDefaultsFor(options.FrameworkCategoryDefaults).OrderByDescending(r => r.Prefix.Length)];
        _diagnosticExempt = StepUpSettings.NonBlank(options.DiagnosticExemptCategories);
        _bufferExcluded = options.EnablePreErrorBuffering ? StepUpSettings.NonBlank(options.PreErrorBufferExcludeCategories) : [];
        _neverTrigger = StepUpSettings.NonBlank(options.NeverTriggerCategories);
        _alwaysExport = StepUpSettings.NonBlank(options.AlwaysExportCategories);
        _hasExportRules = _neverStepUp.Length > 0 || _floors.Length > 0 || _frameworkDefaults.Length > 0 || _bufferExcluded.Length > 0;
    }

    /// <summary>Whether any always-export prefix is configured, so the pipeline registers <see cref="AlwaysExportEnricher"/> only when it can mark an event.</summary>
    internal bool HasAlwaysExport => _alwaysExport.Length > 0;

    /// <summary>
    /// The export minimum and buffer verdict of <paramref name="logEvent"/>: <c>max(switch, NeverStepUp pin unless
    /// diagnostic, category floor)</c>. The pin keeps listed categories at BaseLevel and a floor only raises the
    /// minimum. A floor never passes Warning, so it is not looked up for Warning and above. While diagnostic is active
    /// only a floor of a <c>DiagnosticExemptCategories</c> source applies. A held-back event below Warning is kept out
    /// of the buffer when a framework default not kept in the buffer decided its floor, or its source is listed in
    /// <c>PreErrorBufferExcludeCategories</c>. <paramref name="isDiagnosticActive"/> is read only when a rule can apply.
    /// </summary>
    internal CategoryVerdict Resolve(LogEvent logEvent, LogEventLevel switchLevel, Func<bool>? isDiagnosticActive)
    {
        if (!_hasExportRules || !CategoryPrefix.TryGetSourceContext(logEvent, out var source)) return new(switchLevel, false);

        var diagnosticActive = isDiagnosticActive?.Invoke() ?? false;
        var minimum = !diagnosticActive && CategoryPrefix.MatchesAny(source, _neverStepUp) ? Higher(_baseLevel, switchLevel) : switchLevel;
        if (logEvent.Level >= LogEventLevel.Warning) return new(minimum, false);

        var floor = FloorOf(source, diagnosticActive, out var frameworkExcluded);
        minimum = Higher(minimum, floor);
        var keptOut = logEvent.Level < minimum && (frameworkExcluded || CategoryPrefix.MatchesAny(source, _bufferExcluded));
        return new(minimum, keptOut);
    }

    /// <summary>Whether an Error or Fatal event from this source must not raise the level (<c>NeverTriggerCategories</c>).</summary>
    internal bool NeverTriggers(LogEvent logEvent) => CategoryPrefix.MatchesAny(logEvent, _neverTrigger);

    /// <summary>Whether this source exports at any level through the immediate route (<c>AlwaysExportCategories</c>).</summary>
    internal bool AlwaysExports(LogEvent logEvent) => CategoryPrefix.MatchesAny(logEvent, _alwaysExport);

    private LogEventLevel FloorOf(string source, bool diagnosticActive, out bool frameworkExcluded)
    {
        frameworkExcluded = false;
        if (diagnosticActive && !CategoryPrefix.MatchesAny(source, _diagnosticExempt)) return LogEventLevel.Verbose;

        foreach (var (key, level) in _floors)
        {
            if (CategoryPrefix.Matches(source, key)) return level;
        }

        foreach (var rule in _frameworkDefaults)
        {
            if (CategoryPrefix.Matches(source, rule.Prefix))
            {
                frameworkExcluded = !rule.KeptInBuffer;
                return rule.Floor;
            }
        }
        return LogEventLevel.Verbose;
    }

    private static LogEventLevel Higher(LogEventLevel left, LogEventLevel right) => left > right ? left : right;
}
