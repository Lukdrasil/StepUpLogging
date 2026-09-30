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

public sealed class StepUpLoggingOptions
{
    public StepUpMode Mode { get; set; } = StepUpMode.Auto;
    public string BaseLevel { get; set; } = "Warning";
    public string StepUpLevel { get; set; } = "Information";
    public int DurationSeconds { get; set; } = 180;

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
    /// The sweep sits on the root pipeline, which runs at <c>Verbose</c>, so it costs one regex
    /// replace per pattern per non-excluded string-valued scalar property on every event that reaches the root —
    /// including the events the step-up level switch later drops and never exports — which is why
    /// <see cref="RedactionRegexes"/> is best kept short and each pattern narrow.
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
    /// Enables OpenTelemetry Protocol (OTLP) exporter for production telemetry export.
    /// Configure via environment variables: OTEL_EXPORTER_OTLP_ENDPOINT, OTEL_EXPORTER_OTLP_PROTOCOL
    /// </summary>
    public bool EnableOtlpExporter { get; set; } = true;

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
    /// Logs are formatted as compact JSON.
    /// </summary>
    public bool EnableConsoleLogging { get; set; } = false;

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
    /// least-recently used contexts will be evicted to bound memory usage.
    /// </summary>
    public int PreErrorMaxContexts { get; set; } = 1024;

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
    /// pre-error buffer is never filtered by it — buffered events still flush on error. Set
    /// this to <c>[]</c> to restore pre-3.1.0 behaviour (no category is exempt from step-up).
    /// The default suppresses the Entity Framework Core SQL command log, which would otherwise
    /// flood the export during a step-up window — and, unless <see cref="RedactLogEventProperties"/>
    /// is also set, carry unredacted SQL.
    /// </remarks>
    public string[] NeverStepUpCategories { get; set; } = ["Microsoft.EntityFrameworkCore.Database.Command"];

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
