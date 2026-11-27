using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Marks events the <see cref="CategoryPolicy"/> always exports with <c>IsImmediate=true</c>, so
/// <see cref="BypassSink.Immediate"/> exports them once at any level while <see cref="StepUpSink"/> and the
/// pre-error buffer skip them (ADR 0024).
/// </summary>
internal sealed class AlwaysExportEnricher(CategoryPolicy policy) : ILogEventEnricher
{
    private static readonly LogEventProperty ImmediateProperty = new(LogProperties.IsImmediate, new ScalarValue(true));

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (policy.AlwaysExports(logEvent))
        {
            logEvent.AddOrUpdateProperty(ImmediateProperty);
        }
    }
}
