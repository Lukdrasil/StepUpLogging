using Microsoft.Extensions.Options;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// Fails host start naming every <see cref="EncryptedSpoolOptions"/> requirement that is not met,
/// instead of surfacing on the first audited operation (ADR 0007). Registered by
/// <see cref="StepUpLoggingEncryptedSpoolExtensions.AddEncryptedSpoolAuditSink"/>.
/// </summary>
internal sealed class EncryptedSpoolOptionsValidator : IValidateOptions<EncryptedSpoolOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, EncryptedSpoolOptions options)
    {
        List<string> failures = [];

        RequireNonEmpty(options.SpoolDirectory, nameof(EncryptedSpoolOptions.SpoolDirectory), failures);
        RequireEndpointBaseUrl(options.EndpointBaseUrl, failures);
        RequireNonEmpty(options.ModuleName, nameof(EncryptedSpoolOptions.ModuleName), failures);
        RequireNonEmpty(options.Version, nameof(EncryptedSpoolOptions.Version), failures);
        RequirePositive(options.MaxPayloadBytes, nameof(EncryptedSpoolOptions.MaxPayloadBytes), failures);
        RequirePositive(options.SpoolMaxBytes, nameof(EncryptedSpoolOptions.SpoolMaxBytes), failures);
        RequirePositive(options.SpoolMaxEntries, nameof(EncryptedSpoolOptions.SpoolMaxEntries), failures);
        RequirePositive(options.DrainInterval, nameof(EncryptedSpoolOptions.DrainInterval), failures);
        RequirePositive(options.MaxDrainBackoff, nameof(EncryptedSpoolOptions.MaxDrainBackoff), failures);
        RequirePositive(options.ShutdownDrainTimeout, nameof(EncryptedSpoolOptions.ShutdownDrainTimeout), failures);
        RequirePositive(options.UnreadableRetryLimit, nameof(EncryptedSpoolOptions.UnreadableRetryLimit), failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void RequireNonEmpty(string value, string optionName, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{nameof(EncryptedSpoolOptions)}.{optionName} must be set.");
        }
    }

    private static void RequirePositive(int value, string optionName, List<string> failures)
    {
        if (value <= 0)
        {
            failures.Add($"{nameof(EncryptedSpoolOptions)}.{optionName} must be greater than zero.");
        }
    }

    private static void RequirePositive(long value, string optionName, List<string> failures)
    {
        if (value <= 0)
        {
            failures.Add($"{nameof(EncryptedSpoolOptions)}.{optionName} must be greater than zero.");
        }
    }

    private static void RequirePositive(TimeSpan value, string optionName, List<string> failures)
    {
        if (value <= TimeSpan.Zero)
        {
            failures.Add($"{nameof(EncryptedSpoolOptions)}.{optionName} must be greater than zero.");
        }
    }

    /// <summary>
    /// Non-empty, an absolute URI (the drain worker composes <c>{EndpointBaseUrl}/audit</c> by
    /// string concatenation, so a relative value would surface as a bare <see cref="UriFormatException"/>
    /// out of DI instead of here), and without a query or fragment (either would compose a nonsense
    /// URI silently).
    /// </summary>
    private static void RequireEndpointBaseUrl(string value, List<string> failures)
    {
        var optionName = nameof(EncryptedSpoolOptions.EndpointBaseUrl);

        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{nameof(EncryptedSpoolOptions)}.{optionName} must be set.");
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint))
        {
            failures.Add($"{nameof(EncryptedSpoolOptions)}.{optionName} must be an absolute URI.");
            return;
        }

        if (!string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            failures.Add($"{nameof(EncryptedSpoolOptions)}.{optionName} must not contain a query string or fragment.");
        }
    }
}
