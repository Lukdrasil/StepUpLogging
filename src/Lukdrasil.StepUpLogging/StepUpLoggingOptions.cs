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

public sealed record StepUpLoggingOptions
{
    public StepUpMode Mode { get; init; } = StepUpMode.Auto;
    public string BaseLevel { get; init; } = "Warning";
    public string StepUpLevel { get; init; } = "Information";
    public int DurationSeconds { get; init; } = 180;

    public string[] ExcludePaths { get; init; } = ["/healthz", "/metrics", "/health"];

    public string? ServiceVersion { get; init; }

    public bool EnrichWithEnvironment { get; init; } = true;
    public bool EnrichWithExceptionDetails { get; init; } = true;
    public bool EnrichWithThreadId { get; init; }
    public bool EnrichWithProcessId { get; init; }
    public bool EnrichWithMachineName { get; init; } = true;

    /// <summary>
    /// Regular expression patterns for redacting sensitive data in logs.
    /// Patterns are applied to query strings, headers, route parameters, and request bodies.
    /// </summary>
    public string[] RedactionRegexes { get; init; } = [];

    /// <summary>
    /// Enables capture of request bodies (POST, PUT, PATCH) in logs when logging is stepped-up.
    /// Default: false (to avoid performance impact in normal operation)
    /// </summary>
    public bool CaptureRequestBody { get; init; } = false;

    /// <summary>
    /// Maximum number of bytes to capture from request body. Default: 16KB
    /// </summary>
    public int MaxBodyCaptureBytes { get; init; } = 16 * 1024;

    /// <summary>
    /// Additional sensitive header names to redact in request logging.
    /// Built-in sensitive headers (Authorization, Cookie, X-API-Key, X-Auth-Token, X-Access-Token,
    /// Authorization-Token, Proxy-Authorization, WWW-Authenticate, Sec-WebSocket-Key) are always redacted.
    /// </summary>
    public string[] AdditionalSensitiveHeaders { get; init; } = [];

    /// <summary>
    /// Enables OpenTelemetry Protocol (OTLP) exporter for production telemetry export.
    /// Configure via environment variables: OTEL_EXPORTER_OTLP_ENDPOINT, OTEL_EXPORTER_OTLP_PROTOCOL
    /// </summary>
    public bool EnableOtlpExporter { get; init; } = true;

    /// <summary>
    /// Enables console sink for log output (typically for development/debugging).
    /// Logs are formatted as compact JSON.
    /// </summary>
    public bool EnableConsoleLogging { get; init; } = false;

    /// <summary>
    /// Enables structured exception details in logs (includes stack traces, inner exceptions, etc.)
    /// </summary>
    public bool StructuredExceptionDetails { get; init; } = true;

    /// <summary>
    /// Service instance ID. If not set, uses host name and process ID
    /// </summary>
    public string? ServiceInstanceId { get; init; }

    /// <summary>
    /// Enables pre-error buffering: recent log events are stored in-memory per request/activity
    /// and flushed when an Error/Fatal event occurs. Useful for diagnosing issues by including
    /// context prior to the error.
    /// </summary>
    public bool EnablePreErrorBuffering { get; init; } = true;

    /// <summary>
    /// Maximum number of events to retain per logical context (Activity/Trace). Oldest events
    /// are dropped when the capacity is exceeded.
    /// </summary>
    public int PreErrorBufferSize { get; init; } = 100;

    /// <summary>
    /// Maximum number of concurrent logical contexts tracked by the buffer. When exceeded,
    /// least-recently used contexts will be evicted to bound memory usage.
    /// </summary>
    public int PreErrorMaxContexts { get; init; } = 1024;
}
