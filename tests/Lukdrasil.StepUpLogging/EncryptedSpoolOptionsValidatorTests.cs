using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// The rules <see cref="EncryptedSpoolOptionsValidator"/> applies to the options issue #69 adds:
/// a batch size the receiver can take, and a recheck interval that bounds how often a full spool
/// is scanned.
/// </summary>
public class EncryptedSpoolOptionsValidatorTests
{
    private static EncryptedSpoolOptions ValidOptions() => new()
    {
        SpoolDirectory = "/var/spool/audit",
        EndpointBaseUrl = "https://audit.example/api",
        ModuleName = "orders-api",
        Version = "4.0.0"
    };

    private static void AssertFailsNaming(EncryptedSpoolOptions options, string optionName)
    {
        var result = new EncryptedSpoolOptionsValidator().Validate(name: null, options);

        Assert.True(result.Failed, $"{optionName} was accepted");
        Assert.Contains(result.Failures!, failure => failure.Contains($"{nameof(EncryptedSpoolOptions)}.{optionName}"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1025)]
    public void Validate_DeliveryBatchSizeOutsideOneTo1024_FailsNamingTheOption(int batchSize)
    {
        var options = ValidOptions();
        options.DeliveryBatchSize = batchSize;

        AssertFailsNaming(options, nameof(EncryptedSpoolOptions.DeliveryBatchSize));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1000)]
    public void Validate_SpoolFullRecheckIntervalNotPositive_FailsNamingTheOption(int milliseconds)
    {
        var options = ValidOptions();
        options.SpoolFullRecheckInterval = TimeSpan.FromMilliseconds(milliseconds);

        AssertFailsNaming(options, nameof(EncryptedSpoolOptions.SpoolFullRecheckInterval));
    }
}
