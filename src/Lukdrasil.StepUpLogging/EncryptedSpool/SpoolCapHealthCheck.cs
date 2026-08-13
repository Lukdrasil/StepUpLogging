using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// Reports on the encrypted spool as a whole, worst signal first.
/// <list type="bullet">
/// <item>How full the spool is: healthy below half the cap,
/// <see cref="EncryptedSpoolOptions.SpoolWarnStatus"/> from half on — well before the cap, so an
/// operator still has room to act on whatever is keeping the spool from draining (ADR 0020 D5) —
/// and <see cref="EncryptedSpoolOptions.SpoolFullStatus"/> once records are actually being lost.</item>
/// <item>Whether <c>dead-letter/</c> holds anything: any record in it means an audit record never
/// reached the audit store, which is Unhealthy and not configurable (ADR 0020 D7).</item>
/// <item>Whether the audit endpoint is taking records:
/// <see cref="EncryptedSpoolOptions.UnreachableStatus"/> once the drain worker's attempts have
/// failed often enough in a row (ADR 0019 D4).</item>
/// </list>
/// </summary>
internal sealed class SpoolCapHealthCheck(
    IOptions<EncryptedSpoolOptions> options,
    DeadLetterBox deadLetterBox,
    EndpointReachability reachability) : IHealthCheck
{
    private readonly EncryptedSpoolOptions _options = options.Value;
    private readonly SpoolCapacity _capacity = new(options.Value);

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // The probe reads the spool directory itself, which takes as long as the spool is deep.
        cancellationToken.ThrowIfCancellationRequested();

        Signal[] signals = [SpoolFillSignal(), DeadLetterSignal(), EndpointSignal()];

        // HealthStatus ascends from Unhealthy to Healthy, so the worst signal is the lowest one.
        return Task.FromResult(new HealthCheckResult(
            signals.Min(signal => signal.Status),
            string.Join(' ', signals.Select(signal => signal.Description))));
    }

    private Signal SpoolFillSignal()
    {
        var usage = _capacity.Measure();
        var holdings =
            $"The audit spool at {_options.SpoolDirectory} holds {usage.Records} of {_options.SpoolMaxEntries} records " +
            $"({usage.Bytes} of {_options.SpoolMaxBytes} bytes).";

        return usage switch
        {
            { IsFull: true } => new(_options.SpoolFullStatus, $"{holdings} It is full, so new audit records are being dropped."),
            { IsAtWarnThreshold: true } => new(_options.SpoolWarnStatus, $"{holdings} Spooled records are never rotated out to make room, so once it is full the new audit records are the ones lost."),
            _ => new(HealthStatus.Healthy, holdings)
        };
    }

    private Signal DeadLetterSignal() =>
        deadLetterBox.HoldsRecords
            ? new(
                HealthStatus.Unhealthy,
                $"Audit records the endpoint will never accept are waiting in {deadLetterBox.DirectoryPath}. They never reached the audit store, and nothing removes them: investigate each one and clear the directory by hand.")
            : new(HealthStatus.Healthy, "No audit record has been dead-lettered.");

    private Signal EndpointSignal() =>
        reachability.IsUnreachable
            ? new(
                _options.UnreachableStatus,
                $"The audit endpoint at {_options.EndpointBaseUrl} has failed the last {reachability.ConsecutiveFailures} delivery attempts. No record is lost yet — they stay spooled — but the spool fills for as long as this lasts.")
            : new(HealthStatus.Healthy, "The audit endpoint is taking records.");

    private readonly record struct Signal(HealthStatus Status, string Description);
}
