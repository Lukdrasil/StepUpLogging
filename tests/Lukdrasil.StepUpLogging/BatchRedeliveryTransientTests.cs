using System.Net;
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// A rejected batch is redelivered record by record (ADR 0020 D7); this covers the redelivery
/// meeting a transient answer partway through.
/// </summary>
public class BatchRedeliveryTransientTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    private static AuditEvent AuditedOperation(DateTimeOffset at) =>
        AuditEvent.Success("order.cancel", "user-42", "user") with
        {
            EventId = Guid.CreateVersion7(),
            TimestampUtc = at
        };

    [Fact]
    public async Task DrainAsync_BatchRejectedThenARecordGetsATransientAnswer_StopsTheCycleAndKeepsTheRestInTheSpool()
    {
        using var harness = new SpoolDrainHarness(
            FakeAuditReceiver.Responding(
                HttpStatusCode.UnprocessableEntity,
                HttpStatusCode.OK,
                HttpStatusCode.ServiceUnavailable),
            options => options.DeliveryBatchSize = 64);
        for (var i = 0; i < 4; i++)
        {
            await harness.SpoolAsync(AuditedOperation(Noon.AddSeconds(i)));
        }

        var spooledBefore = harness.SpooledFileNames();

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [harness.BatchEndpoint, harness.AuditEndpoint, harness.AuditEndpoint],
            harness.Receiver.RequestedUris);
        Assert.Equal(spooledBefore.Skip(1), harness.SpooledFileNames());
        Assert.Empty(harness.DeadLetteredFileNames());
    }
}
