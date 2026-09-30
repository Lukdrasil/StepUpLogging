using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Applies <see cref="CompiledRedactionPatterns.Redact(string)"/> to the scalar properties of a log
/// event other than <see cref="ExcludedProperties"/> whose value is a <see cref="string"/> or a
/// URI-like value (<see cref="PathString"/>, <see cref="QueryString"/>, <see cref="HostString"/>,
/// <see cref="Uri"/>), so a secret a consumer logs through an application log, not just through
/// the HTTP request pipeline, is masked before export (ADR 0022 D1). A URI-like value is redacted
/// through its <c>ToString()</c> form (for <see cref="Uri"/> the decoded form the exporter renders)
/// and replaced by the redacted string only when a pattern matched; otherwise it keeps its type.
/// Other non-string scalars and structured, sequence and dictionary values are left untouched;
/// recursing into them is out of scope.
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
            if (property.Value is not ScalarValue { Value: string or PathString or QueryString or HostString or Uri } scalar) continue;

            var original = scalar.Value.ToString()!;
            var redacted = patterns.Redact(original);
            if (!string.Equals(redacted, original, StringComparison.Ordinal))
            {
                logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(property.Key, redacted));
            }
        }
    }
}
