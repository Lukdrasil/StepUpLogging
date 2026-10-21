using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Fails host start with one message per <see cref="StepUpLoggingOptions"/> requirement that is not met, each naming
/// the configuration key and the value it got, so a typo'd level or a non-positive duration surfaces immediately
/// instead of silently falling back (ADR 0007).
/// </summary>
internal sealed class StepUpLoggingOptionsValidator(string sectionName) : IValidateOptions<StepUpLoggingOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, StepUpLoggingOptions options)
    {
        List<string> failures = [];

        RequireDefined(options.Mode, nameof(StepUpLoggingOptions.Mode), failures);
        RequireDefined(options.FrameworkCategoryDefaults, nameof(StepUpLoggingOptions.FrameworkCategoryDefaults), failures);
        RequireDefined(options.ConsoleFormat, nameof(StepUpLoggingOptions.ConsoleFormat), failures);
        RequireDefined(options.ConsoleTraceFormat, nameof(StepUpLoggingOptions.ConsoleTraceFormat), failures);
        RequireDefined(options.RequestSummaryDetail, nameof(StepUpLoggingOptions.RequestSummaryDetail), failures);
        RequireLevel(options.BaseLevel, nameof(StepUpLoggingOptions.BaseLevel), failures);
        RequireLevel(options.StepUpLevel, nameof(StepUpLoggingOptions.StepUpLevel), failures);
        RequireLevel(options.RequestSummaryLevel, nameof(StepUpLoggingOptions.RequestSummaryLevel), failures);
        RequireLevel(options.DiagnosticLevel, nameof(StepUpLoggingOptions.DiagnosticLevel), failures);
        Require(options.DurationSeconds > 0, nameof(StepUpLoggingOptions.DurationSeconds), options.DurationSeconds, "must be greater than 0", failures);
        Require(options.MaxBodyCaptureBytes > 0, nameof(StepUpLoggingOptions.MaxBodyCaptureBytes), options.MaxBodyCaptureBytes, "must be greater than 0", failures);
        Require(options.PreErrorBufferSize > 0, nameof(StepUpLoggingOptions.PreErrorBufferSize), options.PreErrorBufferSize, "must be greater than 0", failures);
        Require(options.PreErrorMaxContexts > 0, nameof(StepUpLoggingOptions.PreErrorMaxContexts), options.PreErrorMaxContexts, "must be greater than 0", failures);
        Require(options.StepUpCooldownSeconds >= 0, nameof(StepUpLoggingOptions.StepUpCooldownSeconds), options.StepUpCooldownSeconds, "must be 0 or more", failures);
        Require(options.MaxContinuousStepUpSeconds == 0 || options.MaxContinuousStepUpSeconds >= options.DurationSeconds,
            nameof(StepUpLoggingOptions.MaxContinuousStepUpSeconds), options.MaxContinuousStepUpSeconds, "must be 0 (no cap) or at least DurationSeconds", failures);
        Require(options.DiagnosticDurationMinutes is >= 1 and <= 120,
            nameof(StepUpLoggingOptions.DiagnosticDurationMinutes), options.DiagnosticDurationMinutes, "must be 1-120", failures);
        RequireFloors(options.CategoryFloors ?? [], failures);
        RequireExemptionsMatchAFloor(options, failures);
        RequirePatterns(options.RedactionRegexes ?? [], failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    internal static bool IsValidLevel(string? value)
        => Enum.GetNames<LogEventLevel>().Contains(value, StringComparer.OrdinalIgnoreCase);

    internal static bool IsValidFloor(string? value)
        => IsValidLevel(value) && Enum.Parse<LogEventLevel>(value!, true) <= LogEventLevel.Warning;

    private void Require(bool valid, string option, object? value, string rule, List<string> failures)
    {
        if (!valid)
        {
            failures.Add($"{sectionName}:{option} {rule}, got '{value}'.");
        }
    }

    private void RequireDefined<TEnum>(TEnum value, string option, List<string> failures)
        where TEnum : struct, Enum
        => Require(Enum.IsDefined(value), option, value, $"must be one of {string.Join(", ", Enum.GetNames<TEnum>())}", failures);

    private void RequireLevel(string? value, string option, List<string> failures)
        => Require(IsValidLevel(value), option, value, "must be a Serilog level name (Verbose, Debug, Information, Warning, Error, Fatal)", failures);

    private void RequireFloors(Dictionary<string, string> floors, List<string> failures)
    {
        foreach (var (key, level) in floors)
        {
            Require(!string.IsNullOrWhiteSpace(key), nameof(StepUpLoggingOptions.CategoryFloors), key, "keys must not be blank", failures);
            Require(IsValidFloor(level), $"{nameof(StepUpLoggingOptions.CategoryFloors)}:{key}", level, "must be a level name no higher than Warning", failures);
        }
    }

    private void RequireExemptionsMatchAFloor(StepUpLoggingOptions options, List<string> failures)
    {
        var floorKeys = (options.CategoryFloors ?? []).Keys;
        var defaults = StepUpLoggingExtensions.FrameworkDefaultsFor(options.FrameworkCategoryDefaults);
        foreach (var exempt in (options.DiagnosticExemptCategories ?? []).Where(e => !string.IsNullOrWhiteSpace(e)))
        {
            var matches = floorKeys.Any(k => CategoryPrefix.Matches(exempt, k)) || defaults.Any(r => CategoryPrefix.Matches(exempt, r.Prefix));
            Require(matches, nameof(StepUpLoggingOptions.DiagnosticExemptCategories), exempt,
                "entries must match a CategoryFloors key or, with FrameworkCategoryDefaults Recommended, a framework default prefix", failures);
        }
    }

    private void RequirePatterns(string[] patterns, List<string> failures)
    {
        foreach (var pattern in patterns.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            Require(Parses(pattern), nameof(StepUpLoggingOptions.RedactionRegexes), pattern, "entries must be valid regular expressions", failures);
        }
    }

    private static bool Parses(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.IgnoreCase);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
