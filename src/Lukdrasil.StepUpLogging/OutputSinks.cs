using Serilog;
using Serilog.Formatting;
using Serilog.Formatting.Compact;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// The library's own output sinks: OTLP, console and file, each behind an async wrapper. Both output loggers, the
/// gated one and the bypass logger, get the same set, each owning its own instances (ADR 0003).
/// </summary>
internal static class OutputSinks
{
    internal static void WriteTo(LoggerConfiguration lc, StepUpLoggingOptions opts, string contentRootPath, string? logFilePath)
    {
        if (opts.EnableOtlpExporter)
        {
            lc.WriteTo.Async(a => a.OpenTelemetry(OtlpExport.ConfigureSink));
        }

        if (opts.EnableConsoleLogging)
        {
            lc.WriteTo.Async(a => a.Console(CreateConsoleFormatter(opts.ConsoleFormat, opts.ConsoleTextProperties ?? [], opts.ConsoleTraceFormat, opts.RequestSummaryDetail)));
        }

        if (!string.IsNullOrWhiteSpace(logFilePath))
        {
            lc.WriteTo.Async(a => a.File(
                formatter: new CompactJsonFormatter(),
                path: Path.Combine(contentRootPath, logFilePath),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                shared: true));
        }
    }

    /// <summary>
    /// The console formatter for <paramref name="format"/>: compact JSON, or one line with the keyed trace id in
    /// <paramref name="traceFormat"/>, the rendered message and the <paramref name="textProperties"/> the event has,
    /// under short line keys when <paramref name="detail"/> is <see cref="RequestSummaryDetail.Minimal"/> (ADR 0034).
    /// The OTLP and file sinks do not use it and stay structured.
    /// </summary>
    internal static ITextFormatter CreateConsoleFormatter(ConsoleLogFormat format, string[] textProperties, ConsoleTraceFormat traceFormat, RequestSummaryDetail detail) => format switch
    {
        ConsoleLogFormat.Text => new ConsoleTextFormatter(StepUpSettings.NonBlank(textProperties), traceFormat, detail == RequestSummaryDetail.Minimal),
        _ => new CompactJsonFormatter(),
    };
}
