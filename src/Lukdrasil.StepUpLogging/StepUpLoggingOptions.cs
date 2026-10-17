using System;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Controls the step-up logging behavior.
/// </summary>
public enum StepUpMode
{
    /// <summary>
    /// Automatically step-up logging level when errors are detected (default production mode).
    /// Logs at BaseLevel normally, steps up to StepUpLevel on errors.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Always log at StepUpLevel (development mode).
    /// Useful for local development to see all detailed logs.
    /// </summary>
    AlwaysOn = 1,

    /// <summary>
    /// Disable step-up mechanism, always log at BaseLevel (minimal logging).
    /// Step-up triggers are ignored.
    /// </summary>
    Disabled = 2,

    /// <summary>
    /// Entered only at startup: the switch sits at <see cref="StepUpLoggingOptions.DiagnosticLevel"/> for
    /// <see cref="StepUpLoggingOptions.DiagnosticDurationMinutes"/>, then the controller runs as <see cref="Auto"/>.
    /// </summary>
    Diagnostic = 3
}

/// <summary>
/// The format of the console sink enabled by <see cref="StepUpLoggingOptions.EnableConsoleLogging"/>.
/// </summary>
public enum ConsoleLogFormat
{
    /// <summary>
    /// Compact JSON (CLEF), one event per line with every property.
    /// </summary>
    Json = 0,

    /// <summary>
    /// One readable line per event: time, level, trace id, <c>SourceContext</c> and the rendered message, with the
    /// exception on the following lines. Of the properties not in the message, only
    /// <see cref="StepUpLoggingOptions.ConsoleTextProperties"/> are written. Line breaks in values are escaped.
    /// </summary>
    Text = 1
}

/// <summary>
/// Selects the built-in framework category rules applied under <see cref="StepUpLoggingOptions.CategoryFloors"/>.
/// </summary>
public enum FrameworkCategoryPreset
{
    /// <summary>
    /// No built-in rules: framework categories behave like application categories.
    /// </summary>
    None = 0,

    /// <summary>
    /// Framework categories whose Information events repeat the request summary or a span (ASP.NET Core hosting,
    /// routing, MVC, HTTP results, static files, HttpClient handlers, Polly, EF Core commands, Npgsql commands)
    /// export at <c>Warning</c> and above and are kept out of the pre-error buffer below <c>Warning</c>.
    /// CORS and the YARP forwarder export at <c>Warning</c> but stay in the buffer, since they explain a failure.
    /// </summary>
    Recommended = 1
}

public sealed class StepUpLoggingOptions
{
    public StepUpMode Mode { get; set; } = StepUpMode.Auto;
    public string BaseLevel { get; set; } = "Warning";
    public string StepUpLevel { get; set; } = "Information";
    public int DurationSeconds { get; set; } = 3;

    /// <summary>
    /// Request paths excluded from request logging, matched case-insensitively. An entry ending in <c>*</c>
    /// matches every path with that prefix. <see langword="null"/> means not configured, and the built-in
    /// defaults <c>["/healthz", "/metrics", "/health"]</c> apply after configuration. A configured list
    /// replaces the defaults, and an empty list excludes nothing.
    /// </summary>
    public string[]? ExcludePaths { get; set; }

    public string? ServiceVersion { get; set; }

    public bool EnrichWithEnvironment { get; set; } = true;
    public bool EnrichWithExceptionDetails { get; set; } = true;
    public bool EnrichWithThreadId { get; set; }
    public bool EnrichWithProcessId { get; set; }
    public bool EnrichWithMachineName { get; set; } = true;
    public bool EnrichWithCallStack { get; set; } = false;

    /// <summary>
    /// Regular expression patterns for redacting sensitive data in logs.
    /// Patterns are applied to the request path (including <c>http.target</c>, the hosting scope's
    /// <c>RequestPath</c> on every event and the <c>Path</c> of
    /// <c>Microsoft.AspNetCore.Hosting.Diagnostics</c> events), query strings,
    /// headers, route parameters, and request bodies, and,
    /// when <see cref="RedactLogEventProperties"/> is also set, to the string-valued and URI-like
    /// (<c>PathString</c>, <c>QueryString</c>, <c>HostString</c>, <c>Uri</c>) scalar properties of
    /// application log events as well.
    /// </summary>
    /// <remarks>
    /// SCOPE: on its own, this covers request metadata (request path, query string, headers, route
    /// values, request body) only, and the request path wherever an exported event repeats it as the
    /// scope <c>RequestPath</c> or the Hosting <c>Path</c>, whatever <see cref="RedactLogEventProperties"/> is set to. A route value whose text sits inside a redacted part of the path
    /// becomes <c>[REDACTED]</c>; <see cref="ExcludePaths"/> still matches the raw path — it does NOT scan the rendered text of arbitrary log messages, so e.g. a secret
    /// passed as a message template argument (<c>logger.LogInformation("token={T}", secret)</c>) is
    /// not redacted. Set <see cref="RedactLogEventProperties"/> to cover that case too; see its own
    /// doc for exactly what it does and does not reach. An interpolated template
    /// (<c>logger.LogInformation($"token={t}")</c>) is never redacted by either setting — it produces
    /// no property. Do not log secrets in interpolated message templates.
    /// </remarks>
    public string[] RedactionRegexes { get; set; } = [];

    /// <summary>
    /// When true, applies <see cref="RedactionRegexes"/> to the string-valued and URI-like
    /// (<c>PathString</c>, <c>QueryString</c>, <c>HostString</c>, <c>Uri</c>) scalar properties of
    /// log events on the root pipeline. A URI-like value is matched on its <c>ToString()</c> form
    /// (for <c>Uri</c> the decoded form) and exported as the redacted string only when a pattern
    /// matched. Default: false.
    /// </summary>
    /// <remarks>
    /// Sweeps log event properties for secrets logged through <c>ILogger</c>, <c>LogImmediate*</c>,
    /// <c>[LoggerMessage]</c> methods, and properties added by your own enrichers — not just the
    /// request-metadata fields the always-on redaction covers. The sweep runs after enrichers.
    /// A value whose redaction fails — a pattern that times out, for example — is replaced by
    /// <c>[REDACTION-ERROR]</c>; any remaining patterns then run against that sentinel and may
    /// rewrite part of it, so the exported text is not always the sentinel verbatim — the original
    /// value is never emitted either way. Enabling this is opt-in so an existing
    /// <see cref="RedactionRegexes"/> configuration does not change behavior on upgrade; with no
    /// patterns configured the flag has no effect either.
    ///
    /// The sweep sits on the root pipeline, behind the same enrichment gate as the library's other
    /// root enrichers: it runs on an event at or above <c>min(live level switch, StepUpLevel, Error)</c>
    /// or marked <c>IsImmediate</c> or <c>IsRequestSummary</c>, and not on an event no sink will use, such as a
    /// Debug event the switch drops. It costs one regex replace per pattern per non-excluded
    /// string-valued scalar property on every such event, which is why <see cref="RedactionRegexes"/> is best
    /// kept short and each pattern narrow; when every pattern is linear-time and they share their options, one
    /// scan for the whole list replaces the per-pattern replaces on a value that matches none of them. The gate is
    /// off when the <c>configure</c> hook is set or <c>Serilog:AuditTo</c> has an entry, and then the sweep runs on
    /// every event that reaches the root. A root <c>Serilog:Filter</c> still sees the properties of a skipped
    /// event unredacted; a filter only decides and exports nothing, but one that logs or forwards those properties
    /// reads the raw values.
    ///
    /// What the sweep does NOT reach:
    /// <list type="bullet">
    /// <item>Message template text and exception messages. An interpolated
    /// <c>logger.LogInformation($"token={t}")</c> bakes the value into the template itself and
    /// produces no property, so it is logged verbatim.</item>
    /// <item>Anything that is not a string or URI-like scalar: other non-string scalars (numbers,
    /// GUIDs, dates) are left as they are, and values held inside a structure
    /// (<c>{@user}</c>, including <c>{@Path}</c>), a sequence or a dictionary are not recursed
    /// into.</item>
    /// <item>The properties the library stamps itself — <c>TraceId</c>, <c>SpanId</c>,
    /// <c>ParentSpanId</c>, <c>TraceFlags</c>, <c>TraceState</c>, <c>SourceContext</c>,
    /// <c>Application</c>, <c>Environment</c>, <c>MachineName</c>, <c>ServiceVersion</c>,
    /// <c>ServiceInstanceId</c> and <c>CallStack</c>. Redacting these would break trace
    /// correlation, OTLP resource identity and the <see cref="NeverStepUpCategories"/> deny-list,
    /// which matches on <c>SourceContext</c>. The exclusion is by property NAME: a property of
    /// your own under one of those names wins over the library's stamp and then escapes redaction
    /// with it.</item>
    /// <item>Events the library writes straight to the bypass logger — the request summary, the
    /// startup warning about level ordering, and the step-up and step-down warnings — which never
    /// pass root enrichment.</item>
    /// </list>
    /// </remarks>
    public bool RedactLogEventProperties { get; set; } = false;

    /// <summary>
    /// Enables capture of request bodies (POST, PUT, PATCH) in logs when logging is stepped-up.
    /// Default: false (to avoid performance impact in normal operation)
    /// </summary>
    public bool CaptureRequestBody { get; set; } = false;

    /// <summary>
    /// Maximum amount of request body to capture. Default: 16KB.
    /// </summary>
    /// <remarks>
    /// Despite the name, this value bounds the number of CHARACTERS read from the UTF-8 decoded
    /// body, not the number of raw bytes: a multi-byte (non-ASCII) body therefore consumes more
    /// underlying bytes than this limit implies. Must be greater than zero — a non-positive value
    /// fails options validation at startup.
    /// </remarks>
    public int MaxBodyCaptureBytes { get; set; } = 16 * 1024;

    /// <summary>
    /// Additional sensitive header names to redact in request logging.
    /// Built-in sensitive headers (Authorization, Cookie, X-API-Key, X-Auth-Token, X-Access-Token,
    /// Authorization-Token, Proxy-Authorization, WWW-Authenticate, Sec-WebSocket-Key) are always redacted.
    /// </summary>
    public string[] AdditionalSensitiveHeaders { get; set; } = [];

    /// <summary>
    /// Enables the OpenTelemetry Protocol (OTLP) log exporter and, unless the app registered the OpenTelemetry SDK
    /// first, the SDK's OTLP exporter. Defaults to <see langword="true"/> when the <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>
    /// environment variable is set and <see langword="false"/> otherwise; set it to <see langword="true"/> to export to
    /// the default <c>localhost:4317</c> without the variable. The endpoint, protocol and headers come from
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>, <c>OTEL_EXPORTER_OTLP_PROTOCOL</c> and <c>OTEL_EXPORTER_OTLP_HEADERS</c>.
    /// </summary>
    public bool EnableOtlpExporter { get; set; } = OtlpEndpointIsSet(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));

    internal static bool OtlpEndpointIsSet(string? endpoint) => !string.IsNullOrWhiteSpace(endpoint);

    /// <summary>
    /// Enables ActivitySource instrumentation for distributed tracing (default: true).
    /// When enabled, activities are created for:
    /// - Request logging (LogRequest, CaptureRequestBody, ApplyRedaction)
    /// - Step-up/step-down transitions (TriggerStepUp, PerformStepDown)
    /// - Buffer operations (FlushBufferedEvents, BufferEvent)
    /// 
    /// Activities are created regardless, but only propagated if registered in OpenTelemetry config.
    /// Set to false to disable activity creation entirely (minimal performance overhead reduction).
    /// </summary>
    public bool EnableActivityInstrumentation { get; set; } = true;

    /// <summary>
    /// Enables console sink for log output (typically for development/debugging).
    /// Logs are formatted as set by <see cref="ConsoleFormat"/>.
    /// </summary>
    public bool EnableConsoleLogging { get; set; } = false;

    /// <summary>
    /// The console sink's format: <see cref="ConsoleLogFormat.Json"/> or <see cref="ConsoleLogFormat.Text"/>.
    /// Only the console changes; the OTLP exporter and the file sink stay structured. Default: <see cref="ConsoleLogFormat.Json"/>.
    /// </summary>
    public ConsoleLogFormat ConsoleFormat { get; set; } = ConsoleLogFormat.Json;

    /// <summary>
    /// Properties written after the message in the <see cref="ConsoleLogFormat.Text"/> console format as
    /// <c>Name=value</c>, when the event has them, so identity such as the token id stays on the console without
    /// OTLP. <see langword="null"/> means not configured, and the built-in default <c>["Jti"]</c> applies after
    /// configuration. A configured list replaces the default; blank entries are ignored.
    /// </summary>
    public string[]? ConsoleTextProperties { get; set; }

    /// <summary>
    /// Enables structured exception details in logs (includes stack traces, inner exceptions, etc.)
    /// </summary>
    public bool StructuredExceptionDetails { get; set; } = true;

    /// <summary>
    /// Service instance ID. If not set, uses host name and process ID
    /// </summary>
    public string? ServiceInstanceId { get; set; }

    /// <summary>
    /// Enables pre-error buffering: recent log events are stored in-memory per request/activity
    /// and flushed when an Error/Fatal event occurs. Useful for diagnosing issues by including
    /// context prior to the error.
    /// </summary>
    /// <remarks>
    /// Only events at or above the resolved <see cref="StepUpLevel"/> are buffered/flushed;
    /// events below that floor are never stored. Set <see cref="StepUpLevel"/> to
    /// <c>"Verbose"</c> to buffer every event.
    /// </remarks>
    public bool EnablePreErrorBuffering { get; set; } = true;

    /// <summary>
    /// Maximum number of events to retain per logical context (Activity/Trace). Oldest events
    /// are dropped when the capacity is exceeded.
    /// </summary>
    /// <remarks>
    /// This limit applies only to events at or above the resolved <see cref="StepUpLevel"/> —
    /// the buffer's implicit level floor — since events below it are never buffered.
    /// </remarks>
    public int PreErrorBufferSize { get; set; } = 100;

    /// <summary>
    /// Maximum number of concurrent logical contexts tracked by the buffer. When exceeded,
    /// the least recently used context of its stripe is evicted to bound memory usage.
    /// </summary>
    /// <remarks>
    /// The contexts are split into one stripe per 64 of the cap, at most 16 (a cap of 1024 or more gives 16 stripes of
    /// at least 64 each); a context belongs to one stripe, so a stripe evicts its own least recently used context while
    /// others are still below their share. Below 128 the buffer uses one stripe holding the whole cap and the eviction
    /// is exactly least recently used.
    /// </remarks>
    public int PreErrorMaxContexts { get; set; } = 1024;

    /// <summary>
    /// <c>SourceContext</c> prefixes whose events below <c>Warning</c> are never held in the pre-error buffer, so an
    /// <c>Error</c> or <c>Fatal</c> in their trace does not export them. Default: empty.
    /// </summary>
    /// <remarks>
    /// Matching is the one ordinal prefix rule of every category option: equal, or followed by a <c>.</c>. Only the
    /// buffer is filtered: an event the switch or a <see cref="CategoryFloors"/> floor lets through is still exported,
    /// so <see cref="StepUpMode.Diagnostic"/> still exports the listed categories at <see cref="DiagnosticLevel"/>, and
    /// their <c>Warning</c> events are still held and flushed. Other categories of the same trace still flush. Blank
    /// entries are ignored.
    /// </remarks>
    public string[] PreErrorBufferExcludeCategories { get; set; } = [];

    /// <summary>
    /// When true, emit a single request summary log for every HTTP request regardless of the BaseLevel.
    /// The summary is emitted via a bypass Serilog logger that is configured to always accept verbose events.
    /// Default: false (opt-in).
    /// </summary>
    public bool AlwaysLogRequestSummary { get; set; } = false;

    /// <summary>
    /// The level to use for request summary logs (e.g., "Information").
    /// </summary>
    public string RequestSummaryLevel { get; set; } = "Information";

    /// <summary>
    /// When true, the logged <c>ClientIp</c> is taken from the first entry of the
    /// <c>X-Forwarded-For</c> header. Only enable this behind a reverse proxy you control AND with
    /// ForwardedHeadersMiddleware configured; the header is client-supplied and spoofable.
    /// Default: false — <c>ClientIp</c> comes from <c>Connection.RemoteIpAddress</c>.
    /// </summary>
    public bool TrustForwardedHeaders { get; set; } = false;

    /// <summary>
    /// When true, a request that completes with a status code &gt;= 500 but no exception is logged at
    /// <c>Error</c> — which also triggers step-up and flushes the pre-error buffer. Set this to false in
    /// a reverse proxy or BFF, where most 5xx responses are relayed from a backend and say nothing about
    /// this application; such requests are then logged at <c>Warning</c> instead. An unhandled exception
    /// is still logged at <c>Error</c> either way. Default: true (existing behaviour).
    /// </summary>
    public bool TreatServerErrorStatusAsError { get; set; } = true;

    /// <summary>
    /// When true, <c>AddStepUpLogging</c> assigns the built logger to Serilog's static <c>Log.Logger</c>,
    /// and disposing the host calls <c>Log.CloseAndFlush()</c>, as before 5.0.0. Default: false, the
    /// static logger is left unchanged; resolve <c>ILogger&lt;T&gt;</c> from DI instead.
    /// Read once at registration from the configuration section and <c>configureOptions</c>.
    /// </summary>
    public bool SetStaticLogger { get; set; } = false;

    /// <summary>
    /// Upper bound, in seconds, on how long step-up may stay continuously active. When exceeded the
    /// level is forced back to BaseLevel and further triggers are ignored for StepUpCooldownSeconds.
    /// Default: 0 (no bound).
    /// </summary>
    public int MaxContinuousStepUpSeconds { get; set; } = 0;

    /// <summary>
    /// Seconds during which triggers are ignored after MaxContinuousStepUpSeconds forces a step-down.
    /// Ignored when the cap is disabled. Default: 300.
    /// </summary>
    public int StepUpCooldownSeconds { get; set; } = 300;

    /// <summary>
    /// Serilog <c>SourceContext</c> prefixes that the step-up must never raise above
    /// <see cref="BaseLevel"/>: while the step-up switch is elevated, events from a listed
    /// category are still exported no lower than <see cref="BaseLevel"/> instead of the
    /// raised step-up level.
    /// </summary>
    /// <remarks>
    /// Matching is ordinal: a category matches when its <c>SourceContext</c> equals a prefix
    /// exactly, or begins with a prefix immediately followed by a <c>.</c> separator. The list
    /// has no effect in <see cref="StepUpMode.AlwaysOn"/> (nothing steps up there), and the
    /// pre-error buffer is never filtered by it — buffered events still flush on error.
    /// <see langword="null"/> means not configured, and the built-in default
    /// <c>["Microsoft.EntityFrameworkCore.Database.Command"]</c> applies after configuration. A configured list,
    /// from appsettings or code, replaces the default; <c>[]</c> pins nothing (pre-3.1.0 behaviour). With
    /// <see cref="FrameworkCategoryDefaults"/> <see cref="FrameworkCategoryPreset.Recommended"/> the EF command
    /// category is already floored at <c>Warning</c>, so the pin matters only with <see cref="FrameworkCategoryPreset.None"/>
    /// or a <see cref="BaseLevel"/> above <c>Warning</c>. The default suppresses the Entity Framework Core SQL command log, which would otherwise
    /// flood the export during a step-up window — and, unless <see cref="RedactLogEventProperties"/>
    /// is also set, carry unredacted SQL.
    /// </remarks>
    public string[]? NeverStepUpCategories { get; set; }

    /// <summary>
    /// <c>SourceContext</c> prefix to level. A floor raises the category's minimum to
    /// <c>max(switch, floor)</c> and never makes it more verbose; the most specific prefix wins.
    /// Keys must be non-blank and values valid levels no higher than <c>Warning</c>. Default: empty.
    /// </summary>
    public Dictionary<string, string> CategoryFloors { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Prefixes that keep their floor during <see cref="StepUpMode.Diagnostic"/>. Each non-blank entry
    /// must be matched by a <see cref="CategoryFloors"/> key. Default: empty.
    /// </summary>
    public string[] DiagnosticExemptCategories { get; set; } = [];

    /// <summary>
    /// Built-in floors for framework categories, consulted only when no <see cref="CategoryFloors"/> key matches the
    /// <c>SourceContext</c>, so a <see cref="CategoryFloors"/> entry overrides a built-in rule, at any level. The rules
    /// are lifted in <see cref="StepUpMode.Diagnostic"/> unless a <see cref="DiagnosticExemptCategories"/> entry keeps
    /// them, and not applied in <see cref="StepUpMode.AlwaysOn"/>. Default: <see cref="FrameworkCategoryPreset.Recommended"/>.
    /// </summary>
    public FrameworkCategoryPreset FrameworkCategoryDefaults { get; set; } = FrameworkCategoryPreset.Recommended;

    /// <summary>
    /// The level the switch sits at during <see cref="StepUpMode.Diagnostic"/>. Default: <c>"Debug"</c>.
    /// </summary>
    public string DiagnosticLevel { get; set; } = "Debug";

    /// <summary>
    /// How long <see cref="StepUpMode.Diagnostic"/> lasts, in minutes, from 1 to 120. Default: 30.
    /// </summary>
    public int DiagnosticDurationMinutes { get; set; } = 30;

    /// <summary>
    /// Prefixes whose events are exported once, at any level, through the immediate path. Default: empty.
    /// </summary>
    public string[] AlwaysExportCategories { get; set; } = [];

    /// <summary>
    /// Prefixes whose <c>Error</c>/<c>Fatal</c> events never trigger step-up; they still export and
    /// flush their trace buffer. Default: empty.
    /// </summary>
    public string[] NeverTriggerCategories { get; set; } = [];
}
