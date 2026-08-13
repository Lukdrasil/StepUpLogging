using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// Reports how full the audit spool is: healthy below half the cap, and
/// <see cref="EncryptedSpoolOptions.SpoolWarnStatus"/> from half on — well before the cap, where
/// new audit records start being dropped, so an operator still has room to act on whatever is
/// keeping the spool from draining (ADR 0020 D5).
/// </summary>
internal sealed class SpoolCapHealthCheck(IOptions<EncryptedSpoolOptions> options) : IHealthCheck
{
    private readonly EncryptedSpoolOptions _options = options.Value;
    private readonly SpoolCapacity _capacity = new(options.Value);

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var usage = _capacity.Measure();
        var holdings =
            $"The audit spool at {_options.SpoolDirectory} holds {usage.Records} of {_options.SpoolMaxEntries} records " +
            $"({usage.Bytes} of {_options.SpoolMaxBytes} bytes).";

        return Task.FromResult(usage.IsAtWarnThreshold
            ? new HealthCheckResult(
                _options.SpoolWarnStatus,
                $"{holdings} Spooled records are never rotated out to make room, so once it is full the new audit records are the ones lost.")
            : HealthCheckResult.Healthy(holdings));
    }
}
