using System.Collections.Frozen;
using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Applies <see cref="CompiledRedactionPatterns.Redact(string)"/> to every string-valued scalar
/// property of a log event, so a secret a consumer logs through an application log — not just
/// through the HTTP request pipeline — is masked before export (ADR 0022 D1). Non-string scalars
/// and structured, sequence and dictionary values are left untouched; recursing into them is out
/// of scope.
/// </summary>
internal sealed class RedactionEnricher(CompiledRedactionPatterns patterns) : ILogEventEnricher
{
    /// <summary>
    /// Properties the library itself stamps that must never be redacted: mangling
    /// <c>ServiceInstanceId</c> — a GUID that can match a hex-pattern secret — would split OTLP
    /// resource identity, and mangling <c>SourceContext</c> would silently break the
    /// <see cref="StepUpLoggingOptions.NeverStepUpCategories"/> deny-list, which matches on it
    /// (ADR 0021, ADR 0022 D6).
    /// </summary>
    private static readonly FrozenSet<string> ExcludedProperties = new[]
    {
        "TraceId", "SpanId", "ParentSpanId", "TraceFlags", "TraceState", "SourceContext",
        "Application", "Environment", "MachineName", "ServiceVersion", "ServiceInstanceId",
    }.ToFrozenSet(StringComparer.Ordinal);

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        // Snapshot first: AddOrUpdateProperty below mutates the same dictionary this would
        // otherwise be enumerating live.
        foreach (var property in logEvent.Properties.ToArray())
        {
            if (ExcludedProperties.Contains(property.Key)) continue;
            if (property.Value is not ScalarValue { Value: string original }) continue;

            var redacted = patterns.Redact(original);
            if (!string.Equals(redacted, original, StringComparison.Ordinal))
            {
                logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(property.Key, redacted));
            }
        }
    }
}
