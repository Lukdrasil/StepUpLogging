using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// Reports how full the audit spool is: healthy below half the cap,
/// <see cref="EncryptedSpoolOptions.SpoolWarnStatus"/> from half on — well before the cap, so an
/// operator still has room to act on whatever is keeping the spool from draining (ADR 0020 D5) —
/// and <see cref="EncryptedSpoolOptions.SpoolFullStatus"/> once records are actually being lost.
/// </summary>
internal sealed class SpoolCapHealthCheck(IOptions<EncryptedSpoolOptions> options) : IHealthCheck
{
    private readonly EncryptedSpoolOptions _options = options.Value;
    private readonly SpoolCapacity _capacity = new(options.Value);

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // The probe reads the spool directory itself, which takes as long as the spool is deep.
        cancellationToken.ThrowIfCancellationRequested();

        var usage = _capacity.Measure();
        var holdings =
            $"The audit spool at {_options.SpoolDirectory} holds {usage.Records} of {_options.SpoolMaxEntries} records " +
            $"({usage.Bytes} of {_options.SpoolMaxBytes} bytes).";

        var (status, description) = usage switch
        {
            { IsFull: true } => (_options.SpoolFullStatus, $"{holdings} It is full, so new audit records are being dropped."),
            { IsAtWarnThreshold: true } => (_options.SpoolWarnStatus, $"{holdings} Spooled records are never rotated out to make room, so once it is full the new audit records are the ones lost."),
            _ => (HealthStatus.Healthy, holdings)
        };

        return Task.FromResult(new HealthCheckResult(status, description));
    }
}
