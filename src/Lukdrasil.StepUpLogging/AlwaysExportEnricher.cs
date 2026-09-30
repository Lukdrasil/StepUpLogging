using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Marks events from the listed <c>SourceContext</c> prefixes with <c>IsImmediate=true</c>, so
/// <see cref="ImmediateSink"/> exports them once at any level while <see cref="StepUpSink"/> and the
/// pre-error buffer skip them (ADR 0024).
/// </summary>
internal sealed class AlwaysExportEnricher(string[] categories) : ILogEventEnricher
{
    private static readonly LogEventProperty ImmediateProperty = new(LogProperties.IsImmediate, new ScalarValue(true));

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (CategoryPrefix.MatchesAny(logEvent, categories))
        {
            logEvent.AddOrUpdateProperty(ImmediateProperty);
        }
    }
}
