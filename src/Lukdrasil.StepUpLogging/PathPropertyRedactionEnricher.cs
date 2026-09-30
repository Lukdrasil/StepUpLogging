using Microsoft.AspNetCore.Http;
using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Applies <see cref="CompiledRedactionPatterns.Redact(string)"/> to the path-bearing properties of
/// every event, whatever <see cref="StepUpLoggingOptions.RedactLogEventProperties"/> is set to:
/// <c>RequestPath</c> on any event, which the hosting log scope pushes onto <c>LogContext</c>, and
/// <c>Path</c> on <c>Microsoft.AspNetCore.Hosting.Diagnostics</c> events. A <see cref="string"/> or
/// <see cref="PathString"/> value is replaced by the redacted string only when a pattern matched.
/// </summary>
internal sealed class PathPropertyRedactionEnricher(CompiledRedactionPatterns patterns) : ILogEventEnricher
{
    private const string HostingDiagnostics = "Microsoft.AspNetCore.Hosting.Diagnostics";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        Redact(logEvent, propertyFactory, "RequestPath");

        if (logEvent.Properties.TryGetValue("SourceContext", out var source)
            && source is ScalarValue { Value: HostingDiagnostics })
        {
            Redact(logEvent, propertyFactory, "Path");
        }
    }

    private void Redact(LogEvent logEvent, ILogEventPropertyFactory propertyFactory, string name)
    {
        var scalar = logEvent.Properties.GetValueOrDefault(name) as ScalarValue;
        if (scalar?.Value is not (string or PathString))
        {
            return;
        }

        var original = scalar.Value.ToString()!;
        var redacted = patterns.Redact(original);
        if (!string.Equals(redacted, original, StringComparison.Ordinal))
        {
            logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(name, redacted));
        }
    }
}
