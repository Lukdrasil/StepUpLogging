using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Resolves the floor of a <c>SourceContext</c>: the <see cref="StepUpLoggingOptions.CategoryFloors"/> entries first,
/// then the framework default rules when no entry matches (ADR 0029).
/// </summary>
internal sealed class CategoryFloorMap
{
    private readonly KeyValuePair<string, LogEventLevel>[] _floors;
    private readonly FrameworkCategoryRule[] _frameworkDefaults;
    private readonly string[] _diagnosticExemptCategories;

    public CategoryFloorMap(IReadOnlyDictionary<string, LogEventLevel> floors, string[] diagnosticExemptCategories, FrameworkCategoryRule[]? frameworkDefaults = null)
    {
        ArgumentNullException.ThrowIfNull(floors);
        ArgumentNullException.ThrowIfNull(diagnosticExemptCategories);
        _floors = [.. floors.OrderByDescending(f => f.Key.Length)];
        _frameworkDefaults = [.. (frameworkDefaults ?? []).OrderByDescending(r => r.Prefix.Length)];
        _diagnosticExemptCategories = [.. diagnosticExemptCategories];
    }

    /// <summary>
    /// Finds the floor of <paramref name="sourceContext"/>; <paramref name="excludedFromBuffer"/> is true when a
    /// framework default that is not kept in the buffer decided it.
    /// </summary>
    public bool TryGetFloor(string sourceContext, bool diagnosticActive, out LogEventLevel floor, out bool excludedFromBuffer)
    {
        floor = default;
        excludedFromBuffer = false;
        if (diagnosticActive && !MatchesAnyExempt(sourceContext)) return false;

        foreach (var (key, level) in _floors)
        {
            if (CategoryPrefix.Matches(sourceContext, key))
            {
                floor = level;
                return true;
            }
        }

        foreach (var rule in _frameworkDefaults)
        {
            if (CategoryPrefix.Matches(sourceContext, rule.Prefix))
            {
                floor = rule.Floor;
                excludedFromBuffer = !rule.KeptInBuffer;
                return true;
            }
        }
        return false;
    }

    private bool MatchesAnyExempt(string sourceContext)
    {
        foreach (var prefix in _diagnosticExemptCategories)
        {
            if (CategoryPrefix.Matches(sourceContext, prefix)) return true;
        }
        return false;
    }
}
