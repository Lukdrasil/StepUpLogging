using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// The <see cref="StepUpLoggingOptions"/> resolved once from <c>IOptions&lt;StepUpLoggingOptions&gt;</c>, after binding,
/// defaults and validation (ADR 0007): typed levels, durations, category prefix lists without blank entries, the
/// category floors, the excluded request paths and the sensitive headers. It is the only reader of the options at
/// runtime; every module reads these instead of interpreting the raw options again.
/// </summary>
internal sealed class StepUpSettings
{
    private static readonly string[] BuiltInSensitiveHeaders =
    [
        "Authorization", "Cookie", "X-API-Key", "X-Auth-Token", "X-Access-Token",
        "Authorization-Token", "Proxy-Authorization", "WWW-Authenticate", "Sec-WebSocket-Key"
    ];

    internal StepUpSettings(StepUpLoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        Mode = options.Mode;
        BaseLevel = Level(options.BaseLevel);
        StepUpLevel = Level(options.StepUpLevel);
        DiagnosticLevel = Level(options.DiagnosticLevel);
        RequestSummaryLevel = Level(options.RequestSummaryLevel);
        StepUpDuration = TimeSpan.FromSeconds(options.DurationSeconds);
        MaxContinuousStepUp = TimeSpan.FromSeconds(options.MaxContinuousStepUpSeconds);
        StepUpCooldown = TimeSpan.FromSeconds(options.StepUpCooldownSeconds);
        DiagnosticDuration = TimeSpan.FromMinutes(options.DiagnosticDurationMinutes);
        NeverStepUpCategories = Mode == StepUpMode.AlwaysOn ? [] : NonBlank(options.NeverStepUpCategories);
        NeverTriggerCategories = NonBlank(options.NeverTriggerCategories);
        PreErrorBufferExcludeCategories = NonBlank(options.PreErrorBufferExcludeCategories);
        AlwaysExportCategories = NonBlank(options.AlwaysExportCategories);
        CategoryFloors = FloorMapOf(options);
        ExcludedPaths = new ExcludedPaths(options.ExcludePaths ?? []);
        SensitiveHeaders = new HashSet<string>([.. BuiltInSensitiveHeaders, .. options.AdditionalSensitiveHeaders ?? []], StringComparer.OrdinalIgnoreCase);
        ActivityInstrumentation = options.EnableActivityInstrumentation;
        TrustForwardedHeaders = options.TrustForwardedHeaders;
        RequestSummaryDetail = options.RequestSummaryDetail;
        AlwaysLogRequestSummary = options.AlwaysLogRequestSummary;
        AlwaysLogOutboundRequestSummary = options.AlwaysLogOutboundRequestSummary;
        TreatServerErrorStatusAsError = options.TreatServerErrorStatusAsError;
        CaptureRequestBody = options.CaptureRequestBody;
        MaxBodyCaptureBytes = options.MaxBodyCaptureBytes;
    }

    /// <summary>
    /// The options as bound, read only while the logger is built: by the pipeline, its output sinks, the redaction
    /// patterns and the startup report. Modules that run per event or per request read the members below.
    /// </summary>
    internal StepUpLoggingOptions Options { get; }

    internal StepUpMode Mode { get; }

    internal LogEventLevel BaseLevel { get; }

    internal LogEventLevel StepUpLevel { get; }

    internal LogEventLevel DiagnosticLevel { get; }

    internal LogEventLevel RequestSummaryLevel { get; }

    /// <summary>The level the switch starts at: <c>StepUpLevel</c> in AlwaysOn, <c>DiagnosticLevel</c> in Diagnostic, else <c>BaseLevel</c>.</summary>
    internal LogEventLevel InitialLevel => Mode switch
    {
        StepUpMode.AlwaysOn => StepUpLevel,
        StepUpMode.Diagnostic => DiagnosticLevel,
        _ => BaseLevel,
    };

    /// <summary>Whether <c>Trigger()</c> can raise the level at all: false in AlwaysOn and Disabled mode.</summary>
    internal bool CanStepUp => Mode is not (StepUpMode.AlwaysOn or StepUpMode.Disabled);

    internal TimeSpan StepUpDuration { get; }

    /// <summary>The longest continuous step-up (ADR 0010); <see cref="TimeSpan.Zero"/> means no cap.</summary>
    internal TimeSpan MaxContinuousStepUp { get; }

    internal TimeSpan StepUpCooldown { get; }

    internal TimeSpan DiagnosticDuration { get; }

    /// <summary>The NeverStepUp prefixes; empty in AlwaysOn mode, which never steps up (ADR 0021).</summary>
    internal string[] NeverStepUpCategories { get; }

    internal string[] NeverTriggerCategories { get; }

    internal string[] PreErrorBufferExcludeCategories { get; }

    internal string[] AlwaysExportCategories { get; }

    /// <summary>The category floors (ADR 0024, ADR 0029), or <see langword="null"/> when no floor applies.</summary>
    internal CategoryFloorMap? CategoryFloors { get; }

    internal ExcludedPaths ExcludedPaths { get; }

    /// <summary>The built-in sensitive headers plus <see cref="StepUpLoggingOptions.AdditionalSensitiveHeaders"/>.</summary>
    internal IReadOnlySet<string> SensitiveHeaders { get; }

    /// <summary>Whether the library starts its own activities (<see cref="StepUpLoggingOptions.EnableActivityInstrumentation"/>).</summary>
    internal bool ActivityInstrumentation { get; }

    internal bool TrustForwardedHeaders { get; }

    internal RequestSummaryDetail RequestSummaryDetail { get; }

    internal bool AlwaysLogRequestSummary { get; }

    internal bool AlwaysLogOutboundRequestSummary { get; }

    internal bool TreatServerErrorStatusAsError { get; }

    internal bool CaptureRequestBody { get; }

    internal int MaxBodyCaptureBytes { get; }

    /// <summary>
    /// Fills the defaults of the options whose default depends on other options; runs as the options PostConfigure.
    /// </summary>
    internal static void ApplyDefaults(StepUpLoggingOptions options)
    {
        options.ExcludePaths ??= ["/healthz", "/metrics", "/health"];
        options.ExcludePaths = [.. options.ExcludePaths, .. options.AdditionalExcludePaths ?? []];
        options.NeverStepUpCategories ??= ["Microsoft.EntityFrameworkCore.Database.Command"];
        string[] textProperties = [LogProperties.Jti, "Method", "Path", "StatusCode", "ElapsedMs"];
        options.ConsoleTextProperties ??= options.RequestSummaryDetail == RequestSummaryDetail.Minimal
            ? [.. textProperties.Select(p => RequestSummary.MinimalKeys[p])]
            : textProperties;
    }

    /// <summary>
    /// The entries that are not blank. A blank category prefix is a configuration typo that would match dot-rooted
    /// <c>SourceContext</c>s and cost a comparison on every event, and a blank pattern or property name means nothing.
    /// </summary>
    internal static string[] NonBlank(IEnumerable<string>? values)
        => [.. (values ?? []).Where(v => !string.IsNullOrWhiteSpace(v))];

    internal static FrameworkCategoryRule[] FrameworkDefaultsFor(FrameworkCategoryPreset preset)
        => preset == FrameworkCategoryPreset.Recommended ? FrameworkCategoryDefaultTable.Recommended : [];

    private static CategoryFloorMap? FloorMapOf(StepUpLoggingOptions options)
    {
        var floors = (options.CategoryFloors ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f.Key))
            .ToDictionary(f => f.Key, f => Enum.Parse<LogEventLevel>(f.Value, ignoreCase: true), StringComparer.Ordinal);
        var frameworkDefaults = options.Mode == StepUpMode.AlwaysOn ? [] : FrameworkDefaultsFor(options.FrameworkCategoryDefaults);
        return floors.Count == 0 && frameworkDefaults.Length == 0
            ? null
            : new CategoryFloorMap(floors, NonBlank(options.DiagnosticExemptCategories), frameworkDefaults);
    }

    // The validator rejects a level that does not parse (ADR 0007), so there is no fallback here.
    private static LogEventLevel Level(string value) => Enum.Parse<LogEventLevel>(value, ignoreCase: true);
}
