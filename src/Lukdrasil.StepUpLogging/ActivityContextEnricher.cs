using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Enriches Serilog log events with additional OpenTelemetry Activity context information
/// that is not provided by Serilog.Enrichers.OpenTelemetry package.
/// Adds ParentSpanId and TraceFlags for complete W3C Trace Context support.
/// </summary>
internal sealed class ActivityContextEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var activity = Activity.Current;
        if (activity == null || activity.IdFormat != ActivityIdFormat.W3C)
            return;

        // Add ParentSpanId - critical for distributed tracing parent-child relationships
        // This allows correlation between parent and child spans across service boundaries
        if (activity.ParentSpanId != default)
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(
                "ParentSpanId", activity.ParentSpanId.ToHexString()));
        }

        // Add TraceFlags - indicates sampling decisions (Recorded=1, NotRecorded=0)
        // Important for understanding if this activity was sampled for detailed tracing
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(
            "TraceFlags", ((int)activity.ActivityTraceFlags).ToString()));

        // Add TraceState if present - vendor-specific trace context
        if (!string.IsNullOrEmpty(activity.TraceStateString))
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(
                "TraceState", activity.TraceStateString));
        }
    }
}