using System;

namespace Lukdrasil.StepUpLogging;

public sealed class StepUpLoggingOptions
{
    public string BaseLevel { get; set; } = "Warning";
    public string StepUpLevel { get; set; } = "Information";
    public int DurationSeconds { get; set; } = 180;

    public string[] ExcludePaths { get; set; } = ["/healthz", "/metrics", "/health"];

    public bool EnrichWithEnvironment { get; set; } = true;
    public string? ServiceVersion { get; set; }

    public string[] RedactionRegexes { get; set; } = [];
    public bool CaptureRequestBody { get; set; } = false;
    public int MaxBodyCaptureBytes { get; set; } = 16 * 1024;

    public bool EnableOtlpExporter { get; set; } = false;
    public bool StructuredExceptionDetails { get; set; } = true;
}
