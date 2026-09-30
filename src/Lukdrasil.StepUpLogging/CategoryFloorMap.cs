using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Resolves the <see cref="StepUpLoggingOptions.CategoryFloors"/> floor of a <c>SourceContext</c>.
/// </summary>
internal sealed class CategoryFloorMap
{
    public CategoryFloorMap(IReadOnlyDictionary<string, LogEventLevel> floors, string[] diagnosticExemptCategories)
    {
        ArgumentNullException.ThrowIfNull(floors);
        ArgumentNullException.ThrowIfNull(diagnosticExemptCategories);
    }

    public bool TryGetFloor(string sourceContext, bool diagnosticActive, out LogEventLevel floor)
    {
        floor = default;
        return false;
    }
}
