using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// What the library writes once at startup through the bypass logger: one Information line with the effective
/// configuration, and a Warning for each key under the options section that binds to nothing and for each option
/// set to a value that has no effect.
/// </summary>
internal static class StartupReport
{
    internal const string SummaryTemplate =
        "StepUp logging: Mode {Mode:l}, BaseLevel {BaseLevel:l}, StepUpLevel {StepUpLevel:l} for {DurationSeconds} s; outputs {Outputs:l}; framework defaults {FrameworkCategoryDefaults:l}; request summary {RequestSummary:l}";

    internal const string UnknownKeyTemplate = "Unknown configuration key {Key:l} is ignored; see the StepUpLoggingOptions properties for valid keys.";

    internal const string UnusedOptionTemplate = "{Option:l} has no effect: {Reason:l}";

    private static readonly HashSet<string> OptionNames = typeof(StepUpLoggingOptions)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(p => p.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static void Write(Serilog.ILogger logger, StepUpLoggingOptions opts, IConfigurationSection section, string? logFilePath)
    {
        logger.Information(SummaryTemplate,
            opts.Mode.ToString(), opts.BaseLevel, opts.StepUpLevel, opts.DurationSeconds, Outputs(opts, logFilePath),
            opts.FrameworkCategoryDefaults.ToString(), opts.AlwaysLogRequestSummary ? $"every request at {opts.RequestSummaryLevel}" : "off");

        foreach (var key in UnknownKeys(section))
        {
            logger.Warning(UnknownKeyTemplate, key);
        }

        foreach (var (option, reason) in UnusedOptions(opts))
        {
            logger.Warning(UnusedOptionTemplate, option, reason);
        }
    }

    internal static IEnumerable<string> UnknownKeys(IConfigurationSection section)
        => section.GetChildren().Where(c => !OptionNames.Contains(c.Key)).Select(c => c.Path);

    private static IEnumerable<(string Option, string Reason)> UnusedOptions(StepUpLoggingOptions opts)
    {
        if (opts.ConsoleFormat != ConsoleLogFormat.Json && !opts.EnableConsoleLogging)
        {
            yield return (nameof(StepUpLoggingOptions.ConsoleFormat), "EnableConsoleLogging is false");
        }

        if (opts.RedactLogEventProperties && !(opts.RedactionRegexes ?? []).Any(p => !string.IsNullOrWhiteSpace(p)))
        {
            yield return (nameof(StepUpLoggingOptions.RedactLogEventProperties), "RedactionRegexes is empty");
        }
    }

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
