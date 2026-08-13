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
/// only the latter registers it, rather than silently deriving client addresses by a second,
/// weaker rule (ADR 0008). An application that forgot it fails at host start, where
/// <c>AddAuditLogging</c>'s start-up validation reports it; a container resolved without ever
/// starting a host still fails here, on the missing dependency.
/// </remarks>
internal sealed class AuditLogger<T>(
    IAuditEventSink sink,
    ILogger<T> logger,
    IHttpContextAccessor httpContextAccessor,
    IOptions<StepUpLoggingOptions> options,
    CompiledRedactionPatterns redactionPatterns) : IAuditLogger<T>
{
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
        AuditWriteResult writeResult;

        try
        {
            writeResult = await sink.WriteAsync(record).ConfigureAwait(false);
        }
        catch
        {
            // Observed only long enough to count it: a consumer wanting continue-on-failure puts
            // that try/catch in their own sink, where it is visible in review.
            AuditMetrics.WriteFailuresCounter.Add(1);
            throw;
        }

        var outcomeTag = new KeyValuePair<string, object?>("outcome", OutcomeTag(record.Outcome));

        switch (writeResult)
        {
            case AuditWriteResult.Stored:
                AuditMetrics.EventsCounter.Add(1, outcomeTag);
                break;

            case AuditWriteResult.Dropped:
                AuditMetrics.EventsDroppedCounter.Add(1, outcomeTag);
                return;

            // Thrown outside the catch above on purpose: audit_write_failures_total means the sink
            // threw while writing, and a sink answering with something no member names may well
            // have written the record. That is a contract violation, not a write failure.
            default:
                throw UnrecognizedWriteResult(writeResult);
        }

        if (companionLog is null) return;

        // Written last and only after a Stored write, so that a companion log can never assert an
        // action for which no audit record exists.
        using (logger.BeginImmediateScope())
        {
            companionLog(logger);
        }
    }

    private InvalidOperationException UnrecognizedWriteResult(AuditWriteResult writeResult) =>
        new($"{sink.GetType()} returned {writeResult} from {nameof(IAuditEventSink.WriteAsync)}, which is neither " +
            $"{nameof(AuditWriteResult)}.{nameof(AuditWriteResult.Stored)} nor " +
            $"{nameof(AuditWriteResult)}.{nameof(AuditWriteResult.Dropped)}. Whether the record exists is now " +
            "unknowable, so no counter is moved and no companion log is written.");

    private AuditEvent Enrich(AuditEvent auditEvent)
    {
        var activity = Activity.Current;
        var (sourceIp, userAgent) = ReadRequestContext();

        return auditEvent with
        {
            EventId = Guid.CreateVersion7(),
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
        // redacted as it is everywhere else in the package. So is the client IP, but only on the
        // branch that took it from X-Forwarded-For — a connection address reaches the sink bare.
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

/// <summary>The audit meter and its instruments, created once for the process.</summary>
/// <remarks>
/// Deliberately not held by <see cref="AuditLogger{T}"/>: a static inside an open generic exists
/// once per closed generic, so an app auditing from three services would create three undisposed
/// meters — the leak <c>ImmediateSink</c> already had to fix once.
/// </remarks>
internal static class AuditMetrics
{
    internal static readonly Meter Meter = new("StepUpLogging.Audit", "1.0.0");

    internal static readonly Counter<long> EventsCounter =
        Meter.CreateCounter<long>("audit_events_total", "count", "Number of audit records written, by outcome");

    internal static readonly Counter<long> EventsDroppedCounter =
        Meter.CreateCounter<long>("audit_events_dropped_total", "count", "Number of audit records a sink deliberately discarded, by outcome");

    internal static readonly Counter<long> WriteFailuresCounter =
        Meter.CreateCounter<long>("audit_write_failures_total", "count", "Number of audit record writes that failed");
}
