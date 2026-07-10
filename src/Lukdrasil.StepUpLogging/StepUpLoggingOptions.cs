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
    Disabled = 2
}

public sealed class StepUpLoggingOptions
{
    public StepUpMode Mode { get; set; } = StepUpMode.Auto;
    public string BaseLevel { get; set; } = "Warning";
    public string StepUpLevel { get; set; } = "Information";
    public int DurationSeconds { get; set; } = 180;

    public string[] ExcludePaths { get; set; } = ["/healthz", "/metrics", "/health"];

    public string? ServiceVersion { get; set; }

    public bool EnrichWithEnvironment { get; set; } = true;
    public bool EnrichWithExceptionDetails { get; set; } = true;
    public bool EnrichWithThreadId { get; set; }
    public bool EnrichWithProcessId { get; set; }
    public bool EnrichWithMachineName { get; set; } = true;
    public bool EnrichWithCallStack { get; set; } = false;

    /// <summary>
    /// Regular expression patterns for redacting sensitive data in logs.
    /// Patterns are applied to query strings, headers, route parameters, and request bodies.
    /// </summary>
    /// <remarks>
    /// SCOPE: redaction covers request metadata (query string, headers, route values, request body) only.
    /// It does NOT scan the rendered text of arbitrary log messages — e.g. a secret passed as a message
    /// template argument (<c>logger.LogInformation("token={T}", secret)</c>) is not redacted. Do not log
    /// secrets in message templates.
    /// </remarks>
    public string[] RedactionRegexes { get; set; } = [];

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
    /// flood the export — and carry unredacted SQL — during a step-up window.
    /// </remarks>
    public string[] NeverStepUpCategories { get; set; } = ["Microsoft.EntityFrameworkCore.Database.Command"];
}
