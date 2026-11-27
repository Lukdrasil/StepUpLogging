using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// What the library writes once at startup through the bypass logger: one Information line with the effective
/// configuration unless <see cref="StepUpLoggingOptions.WriteStartupSummary"/> is false, a Warning for each key
/// under the options section that binds to nothing and for each option set to a value that has no effect, a Warning
/// when a level cannot raise verbosity (ADR 0007 warn-not-fail), and in Diagnostic mode a Warning with its window.
/// </summary>
internal static class StartupReport
{
    internal const string SummaryTemplate =
        "StepUp logging: Mode {Mode:l}, BaseLevel {BaseLevel:l}, StepUpLevel {StepUpLevel:l} for {DurationSeconds} s; outputs {Outputs:l}; framework defaults {FrameworkCategoryDefaults:l}; client IP logs {ClientIpLogging:l}, audit {ClientIpAudit:l}; request summary {RequestSummary:l}";

    internal const string UnknownKeyTemplate = "Unknown configuration key {Key:l} is ignored; see the StepUpLoggingOptions properties for valid keys.";

    internal const string UnusedOptionTemplate = "{Option:l} has no effect: {Reason:l}";

    private static readonly HashSet<string> OptionNames = typeof(StepUpLoggingOptions)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(p => p.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly (string Option, string Reason, Func<StepUpLoggingOptions, bool> Applies)[] UnusedOptionRules =
    [
        (nameof(StepUpLoggingOptions.ConsoleFormat), "EnableConsoleLogging is false",
            opts => opts.ConsoleFormat != ConsoleLogFormat.Json && !opts.EnableConsoleLogging),
        (nameof(StepUpLoggingOptions.RedactLogEventProperties), "RedactionRegexes is empty",
            opts => opts.RedactLogEventProperties && StepUpSettings.NonBlank(opts.RedactionRegexes).Length == 0),
        (nameof(StepUpLoggingOptions.RequestSummaryDetail), "AlwaysLogRequestSummary and AlwaysLogOutboundRequestSummary are false",
            opts => opts.RequestSummaryDetail != RequestSummaryDetail.Full && !opts.AlwaysLogRequestSummary && !opts.AlwaysLogOutboundRequestSummary),
        (nameof(StepUpLoggingOptions.RequestSummaryOnError), "AlwaysLogRequestSummary is false",
            opts => opts.RequestSummaryOnError != RequestSummaryOnErrorMode.Keep && !opts.AlwaysLogRequestSummary),
    ];

    internal static void Write(Serilog.ILogger logger, StepUpSettings settings, IConfigurationSection section, string? logFilePath, DateTimeOffset diagnosticExpiresAt)
    {
        var opts = settings.Options;
        if (opts.WriteStartupSummary)
        {
            logger.Information(SummaryTemplate,
                opts.Mode.ToString(), opts.BaseLevel, opts.StepUpLevel, opts.DurationSeconds, Outputs(opts, logFilePath),
                opts.FrameworkCategoryDefaults.ToString(), opts.ClientIpLogging.ToString(), opts.ClientIpAudit.ToString(), RequestSummaryText(opts));
        }

        foreach (var key in UnknownKeys(section))
        {
            logger.Warning(UnknownKeyTemplate, key);
        }

        foreach (var (option, reason) in UnusedOptions(opts))
        {
            logger.Warning(UnusedOptionTemplate, option, reason);
        }

        WriteLevelWarnings(logger, settings, diagnosticExpiresAt);
    }

    // A StepUpLevel or DiagnosticLevel not strictly more verbose than BaseLevel (numerically >= it, since a more
    // verbose level is a lower LogEventLevel) cannot raise verbosity. AlwaysOn and Disabled ignore the ordering.
    private static void WriteLevelWarnings(Serilog.ILogger logger, StepUpSettings settings, DateTimeOffset diagnosticExpiresAt)
    {
        if (settings.Mode is StepUpMode.Auto or StepUpMode.Diagnostic && settings.StepUpLevel >= settings.BaseLevel)
        {
            logger.Warning(
                "StepUpLevel {StepUpLevel} is not more verbose than BaseLevel {BaseLevel}; step-up cannot increase verbosity in {Mode:l} mode.",
                settings.StepUpLevel, settings.BaseLevel, settings.Mode.ToString());
        }

        if (settings.Mode is not StepUpMode.Diagnostic)
        {
            return;
        }

        if (settings.DiagnosticLevel >= settings.BaseLevel)
        {
            logger.Warning(
                "DiagnosticLevel {DiagnosticLevel} is not more verbose than BaseLevel {BaseLevel}; Diagnostic mode cannot increase verbosity.",
                settings.DiagnosticLevel, settings.BaseLevel);
        }

        logger.Warning(
            "StepUp Diagnostic mode active at {DiagnosticLevel} for {DurationMinutes} min until {ExpiresAt}",
            settings.DiagnosticLevel, settings.Options.DiagnosticDurationMinutes, diagnosticExpiresAt);
    }

    internal static IEnumerable<string> UnknownKeys(IConfigurationSection section)
        => section.GetChildren().Where(c => !OptionNames.Contains(c.Key)).Select(c => c.Path);

    private static IEnumerable<(string Option, string Reason)> UnusedOptions(StepUpLoggingOptions opts)
        => UnusedOptionRules.Where(rule => rule.Applies(opts)).Select(rule => (rule.Option, rule.Reason));

    private static string RequestSummaryText(StepUpLoggingOptions opts)
        => (opts.AlwaysLogRequestSummary, opts.AlwaysLogOutboundRequestSummary) switch
        {
            (false, false) => "off",
            (true, false) => $"inbound at {opts.RequestSummaryLevel}, {opts.RequestSummaryDetail}{OnErrorText(opts)}",
            (false, true) => $"outbound at {opts.RequestSummaryLevel}, {opts.RequestSummaryDetail}",
            (true, true) => $"inbound and outbound at {opts.RequestSummaryLevel}, {opts.RequestSummaryDetail}{OnErrorText(opts)}",
        };

    private static string OnErrorText(StepUpLoggingOptions opts)
        => opts.RequestSummaryOnError == RequestSummaryOnErrorMode.Omit ? ", omitted on error" : string.Empty;

    private static string Outputs(StepUpLoggingOptions opts, string? logFilePath)
    {
        List<string> outputs = [];
        if (opts.EnableOtlpExporter)
        {
            outputs.Add($"OTLP {OtlpEndpoint(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"))}");
        }

        if (opts.EnableConsoleLogging)
        {
            outputs.Add($"Console {opts.ConsoleFormat}");
        }

        if (!string.IsNullOrWhiteSpace(logFilePath))
        {
            outputs.Add($"File {logFilePath}");
        }

        return outputs.Count == 0 ? "none" : string.Join(", ", outputs);
    }

    // Scheme, host and port only, so credentials or a token in the URI never reach the log.
    internal static string OtlpEndpoint(string? endpoint)
        => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Authority}" : "default localhost";
}
