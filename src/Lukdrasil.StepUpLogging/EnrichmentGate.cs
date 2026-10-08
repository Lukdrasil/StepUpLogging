using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Decides whether the Verbose root pipeline enriches an event (ADR 0026). An event no sink will
/// export is dropped before the root enrichers run: its level is below the live step-up switch, below
/// <c>StepUpLevel</c> (the lowest level the pre-error buffer holds), below <c>Error</c> (the trigger
/// and the buffer flush read it), and it carries neither the immediate nor the request-summary marker.
/// </summary>
internal sealed class EnrichmentGate(LoggingLevelSwitch levelSwitch, LogEventLevel stepUpLevel)
{
    // The last event Needs rejected on this thread. Serilog runs the enrichers, the sinks and so StepUpSink on
    // the calling thread, so StepUpSink finds out through TakeSkipped that no enricher ran on its event. One slot:
    // a nested rejection (a root filter or SelfLog handler that logs) replaces it (ADR 0026 D3).
    [ThreadStatic]
    private static LogEvent? _skipped;

    public bool Needs(LogEvent logEvent)
    {
        if (Accepts(logEvent)) return true;
        _skipped = logEvent;
        return false;
    }

    /// <summary>Takes the event the gate last rejected on this thread when it is <paramref name="logEvent"/>.</summary>
    internal static bool TakeSkipped(LogEvent logEvent)
    {
        if (!ReferenceEquals(_skipped, logEvent)) return false;
        _skipped = null;
        return true;
    }

    private bool Accepts(LogEvent logEvent)
        => logEvent.Level >= Floor()
           || LogProperties.HasFlag(logEvent, LogProperties.IsImmediate)
           || LogProperties.HasFlag(logEvent, LogProperties.IsRequestSummary);

    private LogEventLevel Floor() => (LogEventLevel)Math.Min(
        Math.Min((int)levelSwitch.MinimumLevel, (int)stepUpLevel),
        (int)LogEventLevel.Error);

    /// <summary>
    /// Returns the predicate for the root enrichers. A consumer root sink (the configure hook) or a
    /// <c>Serilog:AuditTo</c> sink sees every Verbose event, so either one keeps enrichment on for all.
    /// </summary>
    internal static Func<LogEvent, bool> For(
        LoggingLevelSwitch levelSwitch,
        LogEventLevel stepUpLevel,
        Action<IServiceProvider, LoggerConfiguration>? configure,
        IConfiguration configuration)
        => configure is not null || HasAuditSinks(configuration)
            ? static _ => true
            : new EnrichmentGate(levelSwitch, stepUpLevel).Needs;

    private static bool HasAuditSinks(IConfiguration configuration)
        => configuration.GetSection("Serilog:AuditTo").GetChildren().Any();
}
