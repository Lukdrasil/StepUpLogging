using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// The <see cref="StepUpLoggingOptions"/> resolved once from <c>IOptions&lt;StepUpLoggingOptions&gt;</c>, after binding,
/// defaults and validation (ADR 0007): typed levels, durations, the <see cref="CategoryPolicy"/>, the excluded
/// request paths and the sensitive headers. It is the only reader of the options at runtime; every module reads these
/// instead of interpreting the raw options again.
/// </summary>
internal sealed class StepUpSettings
{
    private static readonly string[] BuiltInSensitiveHeaders =
    [
        "Authorization", "Cookie", "X-API-Key", "X-Auth-Token", "X-Access-Token",
        "Authorization-Token", "Proxy-Authorization", "WWW-Authenticate", "Sec-WebSocket-Key"
    ];

    // Redacted in the logged headers unless the client IP mode of the request is Full (ADR 0042 D5, ADR 0044).
    private static readonly string[] ClientIpHeaders = ["X-Forwarded-For", "Forwarded", "X-Real-IP", "X-Original-For"];

    private readonly ClientIpMode _clientErrorIpLogging;
    private readonly IReadOnlySet<string> _sensitiveHeaders;
    private readonly IReadOnlySet<string> _clientErrorSensitiveHeaders;

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
        Categories = new CategoryPolicy(options);
        ExcludedPaths = new ExcludedPaths(options.ExcludePaths ?? []);
        ClientIpLogging = options.ClientIpLogging;
        _clientErrorIpLogging = (ClientIpMode)Math.Max((int)options.ClientIpLogging, (int)options.ClientIpLoggingOnClientError);
        _sensitiveHeaders = SensitiveHeadersUnder(ClientIpLogging, options.AdditionalSensitiveHeaders);
        _clientErrorSensitiveHeaders = SensitiveHeadersUnder(_clientErrorIpLogging, options.AdditionalSensitiveHeaders);
        ActivityInstrumentation = options.EnableActivityInstrumentation;
        TrustForwardedHeaders = options.TrustForwardedHeaders;
        ClientIpAudit = options.ClientIpAudit;
        RequestSummaryDetail = options.RequestSummaryDetail;
        AlwaysLogRequestSummary = options.AlwaysLogRequestSummary;
        RequestSummaryOnError = options.RequestSummaryOnError;
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

    /// <summary>Every category option resolved into one policy (ADR 0021, ADR 0024, ADR 0028, ADR 0029).</summary>
    internal CategoryPolicy Categories { get; }

    internal ExcludedPaths ExcludedPaths { get; }

    /// <summary>
    /// The built-in sensitive headers, the client IP headers unless the client IP mode for <paramref name="status"/>
    /// is <see cref="ClientIpMode.Full"/>, plus <see cref="StepUpLoggingOptions.AdditionalSensitiveHeaders"/>.
    /// </summary>
    internal IReadOnlySet<string> SensitiveHeadersFor(int status) => IsClientError(status) ? _clientErrorSensitiveHeaders : _sensitiveHeaders;

    /// <summary>
    /// The client IP mode of the logs for a request answered with <paramref name="status"/>: on a client error
    /// request (400 to 499) the higher of <c>ClientIpLogging</c> and <c>ClientIpLoggingOnClientError</c> (ADR 0044),
    /// else <c>ClientIpLogging</c>.
    /// </summary>
    internal ClientIpMode ClientIpLoggingFor(int status) => IsClientError(status) ? _clientErrorIpLogging : ClientIpLogging;

    private static bool IsClientError(int status) => status is >= 400 and <= 499;

    private static HashSet<string> SensitiveHeadersUnder(ClientIpMode mode, string[]? additional)
        => new([.. BuiltInSensitiveHeaders, .. mode == ClientIpMode.Full ? [] : ClientIpHeaders, .. additional ?? []], StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the library starts its own activities (<see cref="StepUpLoggingOptions.EnableActivityInstrumentation"/>).</summary>
    internal bool ActivityInstrumentation { get; }

    internal bool TrustForwardedHeaders { get; }

    internal ClientIpMode ClientIpLogging { get; }

    internal ClientIpMode ClientIpAudit { get; }

    internal RequestSummaryDetail RequestSummaryDetail { get; }

    internal bool AlwaysLogRequestSummary { get; }

    internal RequestSummaryOnErrorMode RequestSummaryOnError { get; }

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
        options.RedactionExemptProperties ??= [LogProperties.Jti, LogProperties.Sid];
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

    // The validator rejects a level that does not parse (ADR 0007), so there is no fallback here.
    internal static LogEventLevel Level(string value) => Enum.Parse<LogEventLevel>(value, ignoreCase: true);
}
