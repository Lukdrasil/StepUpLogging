using System.Collections.Frozen;
using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Applies <see cref="CompiledRedactionPatterns.Redact(string)"/> to the string-valued scalar
/// properties of a log event other than <see cref="ExcludedProperties"/>, so a secret a consumer
/// logs through an application log — not just through the HTTP request pipeline — is masked before
/// export (ADR 0022 D1). Non-string scalars and structured, sequence and dictionary values are left
/// untouched; recursing into them is out of scope.
/// </summary>
internal sealed class RedactionEnricher(CompiledRedactionPatterns patterns) : ILogEventEnricher
{
    /// <summary>
    /// Properties the library itself stamps that must never be redacted: mangling
    /// <c>ServiceInstanceId</c> — a GUID that can match a hex-pattern secret — would split OTLP
    /// resource identity, mangling <c>SourceContext</c> would silently break the
    /// <see cref="StepUpLoggingOptions.NeverStepUpCategories"/> deny-list, which matches on it
    /// (ADR 0021, ADR 0022 D6), and mangling <c>CallStack</c> would leave the stack the library
    /// recorded unreadable while protecting nothing.
    /// </summary>
    // ponytail: matching by name means a consumer property under one of these names wins over the
    // library's AddPropertyIfAbsent stamp and escapes redaction with it. Accepted in D6 — telling
    // the two apart needs per-event state the enricher deliberately does not carry; the public doc
    // on StepUpLoggingOptions.RedactLogEventProperties names the hole so it is not silent.
    private static readonly FrozenSet<string> ExcludedProperties = new[]
    {
        "TraceId", "SpanId", "ParentSpanId", "TraceFlags", "TraceState", "SourceContext",
        "Application", "Environment", "MachineName", "ServiceVersion", "ServiceInstanceId",
        "CallStack",
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
