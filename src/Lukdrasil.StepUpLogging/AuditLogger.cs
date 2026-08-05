using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Writes audit records straight to the registered <see cref="IAuditEventSink"/>, bypassing
/// Serilog so the write can be awaited and its failure propagated to the business call site.
/// </summary>
/// <remarks>
/// The dependency on <see cref="CompiledRedactionPatterns"/> is what makes
/// <see cref="StepUpLoggingExtensions.AddAuditLogging{TSink}"/> require
/// <see cref="StepUpLoggingExtensions.AddStepUpLogging(Microsoft.Extensions.Hosting.IHostApplicationBuilder, Action{StepUpLoggingOptions}?, string, string?)"/>:
/// only the latter registers it, so an app that forgot it fails when the first audit logger is
/// resolved rather than silently deriving client addresses by a second, weaker rule (ADR 0008).
/// </remarks>
internal sealed class AuditLogger<T>(
    IAuditEventSink sink,
    ILogger<T> logger,
    IHttpContextAccessor httpContextAccessor,
    IOptions<StepUpLoggingOptions> options,
    CompiledRedactionPatterns redactionPatterns) : IAuditLogger<T>
{
    // Static so the meter and its instruments are created once for the process: IAuditLogger<T> is
    // registered Scoped, so per-instance instruments would leak one set per request.
    private static readonly Meter Meter = new("StepUpLogging.Audit", "1.0.0");
    private static readonly Counter<long> EventsCounter = Meter.CreateCounter<long>("audit_events_total", "count", "Number of audit records written, by outcome");
    private static readonly Counter<long> WriteFailuresCounter = Meter.CreateCounter<long>("audit_write_failures_total", "count", "Number of audit record writes that failed");

    /// <inheritdoc />
    public ValueTask AuditAsync(AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        return WriteThenLogAsync(auditEvent, companionLog: null);
    }

    /// <inheritdoc />
    public ValueTask AuditAsync(AuditEvent auditEvent, Action<ILogger> log)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        ArgumentNullException.ThrowIfNull(log);
        return WriteThenLogAsync(auditEvent, log);
    }

    private async ValueTask WriteThenLogAsync(AuditEvent auditEvent, Action<ILogger>? companionLog)
    {
        var record = Enrich(auditEvent);

        try
        {
            await sink.WriteAsync(record);
        }
        catch
        {
            // Observed only long enough to count it: a consumer wanting continue-on-failure puts
            // that try/catch in their own sink, where it is visible in review.
            WriteFailuresCounter.Add(1);
            throw;
        }

        EventsCounter.Add(1, new KeyValuePair<string, object?>("outcome", OutcomeTag(record.Outcome)));

        if (companionLog is null) return;

        // Written last and only on success, so that a companion log can never assert an action for
        // which no audit record exists.
        using (logger.BeginImmediateScope())
        {
            companionLog(logger);
        }
    }

    private AuditEvent Enrich(AuditEvent auditEvent)
    {
        var activity = Activity.Current;
        var (sourceIp, userAgent) = ReadRequestContext();

        return auditEvent with
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            TraceId = activity?.TraceId.ToString(),
            SpanId = activity?.SpanId.ToString(),
            SourceIp = sourceIp,
            UserAgent = userAgent
        };
    }

    private (string? SourceIp, string? UserAgent) ReadRequestContext()
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null) return (null, null);

        // Deliberately not Connection.RemoteIpAddress: ADR 0008 allows exactly one client-IP rule
        // in the package, so the audit trail resolves the client the same way request logging does
        // (including the TrustForwardedHeaders decision).
        var (clientIp, _) = StepUpLoggingExtensions.ExtractClientAddresses(
            httpContext, options.Value.TrustForwardedHeaders, redactionPatterns);
        var rawUserAgent = StepUpLoggingExtensions.ExtractUserAgent(httpContext.Request);

        // Redaction follows the origin of the value: the User-Agent is client-supplied and is
        // redacted as it is everywhere else in the package, while the connection address is not.
        return (clientIp, rawUserAgent is null ? null : redactionPatterns.Redact(rawUserAgent));
    }

    /// <summary>Maps the outcome to its metric tag, keeping the counter's cardinality bounded.</summary>
    private static string OutcomeTag(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Success => nameof(AuditOutcome.Success),
        AuditOutcome.Failure => nameof(AuditOutcome.Failure),
        AuditOutcome.Denied => nameof(AuditOutcome.Denied),
        _ => "Unknown"
    };
}
