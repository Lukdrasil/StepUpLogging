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

    /// <summary>
    /// Regular expression patterns for redacting sensitive data in logs.
    /// Patterns are applied to query strings, headers, route parameters, and request bodies.
    /// </summary>
    public string[] RedactionRegexes { get; set; } = [];

    /// <summary>
    /// Enables capture of request bodies (POST, PUT, PATCH) in logs when logging is stepped-up.
    /// Default: false (to avoid performance impact in normal operation)
    /// </summary>
    public bool CaptureRequestBody { get; set; } = false;

    /// <summary>
    /// Maximum number of bytes to capture from request body. Default: 16KB
    /// </summary>
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
    public bool EnablePreErrorBuffering { get; set; } = true;

    /// <summary>
    /// Maximum number of events to retain per logical context (Activity/Trace). Oldest events
    /// are dropped when the capacity is exceeded.
    /// </summary>
    public int PreErrorBufferSize { get; set; } = 100;

    /// <summary>
    /// Maximum number of concurrent logical contexts tracked by the buffer. When exceeded,
    /// least-recently used contexts will be evicted to bound memory usage.
    /// </summary>
    public int PreErrorMaxContexts { get; set; } = 1024;
}
