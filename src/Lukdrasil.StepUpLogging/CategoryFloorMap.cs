using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Resolves the <see cref="StepUpLoggingOptions.CategoryFloors"/> floor of a <c>SourceContext</c>.
/// </summary>
internal sealed class CategoryFloorMap
{
    private readonly KeyValuePair<string, LogEventLevel>[] _floors;
    private readonly string[] _diagnosticExemptCategories;

    public CategoryFloorMap(IReadOnlyDictionary<string, LogEventLevel> floors, string[] diagnosticExemptCategories)
    {
        ArgumentNullException.ThrowIfNull(floors);
        ArgumentNullException.ThrowIfNull(diagnosticExemptCategories);
        _floors = [.. floors.OrderByDescending(f => f.Key.Length)];
        _diagnosticExemptCategories = [.. diagnosticExemptCategories];
    }

    public bool TryGetFloor(string sourceContext, bool diagnosticActive, out LogEventLevel floor)
    {
        floor = default;
        if (diagnosticActive && !MatchesAnyExempt(sourceContext)) return false;

        foreach (var (key, level) in _floors)
        {
            if (CategoryPrefix.Matches(sourceContext, key))
            {
                floor = level;
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
