using System.Net;
using System.Text.Json;
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Batched delivery (issue #69): the drain worker posts the stored bytes unchanged, one record to
/// <c>/audit</c> or up to <see cref="EncryptedSpoolOptions.DeliveryBatchSize"/> of them as one JSON
/// array to <c>/audit/batch</c>, and the delivery contract of ADR 0020 D7 holds for the batch as a
/// whole.
/// </summary>
public class BatchedDrainTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    private static AuditEvent AuditedOperation(DateTimeOffset at) =>
        AuditEvent.Success("order.cancel", "user-42", "user") with
        {
            EventId = Guid.CreateVersion7(),
            TimestampUtc = at
        };

    private static async Task<IReadOnlyList<AuditEvent>> SpoolInOrderAsync(SpoolDrainHarness harness, int count)
    {
        List<AuditEvent> spooled = [];
        for (var i = 0; i < count; i++)
        {
            spooled.Add(await harness.SpoolAsync(AuditedOperation(Noon.AddSeconds(i))));
        }

        return spooled;
    }

    private static Action<EncryptedSpoolOptions> BatchesOf(int size) => options => options.DeliveryBatchSize = size;

    [Fact]
    public async Task DrainAsync_BatchSizeOne_PostsTheStoredBytesUnchangedWithAContentLength()
    {
        using var harness = new SpoolDrainHarness(FakeAuditReceiver.Responding(HttpStatusCode.OK));
        await harness.SpoolAsync(AuditedOperation(Noon));
        var stored = File.ReadAllBytes(Path.Combine(harness.SpoolOptions.SpoolDirectory, harness.SpooledFileNames()[0]));

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(harness.Receiver.Requests);
        Assert.Equal(harness.AuditEndpoint, request.Uri);
        Assert.Equal(stored, request.Body);

        // What PostAsJsonAsync sent before: the same bytes, the same media type, now framed by
        // Content-Length instead of chunked.
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(Assert.Single(harness.Receiver.Received), JsonSerializerOptions.Web), request.Body);
        Assert.Equal("application/json; charset=utf-8", request.ContentType);
        Assert.Equal(request.Body.Length, request.ContentLength);
    }

    [Fact]
    public async Task DrainAsync_SeventyRecordsInBatchesOf64_SecondBatchUnavailable_DeletesTheFirst64AndKeepsTheLastSix()
    {
        using var harness = new SpoolDrainHarness(
            FakeAuditReceiver.Responding(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable),
            BatchesOf(64));
        var spooled = await SpoolInOrderAsync(harness, 70);
        var lastSix = harness.SpooledFileNames().Skip(64).ToList();

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal([harness.BatchEndpoint, harness.BatchEndpoint], harness.Receiver.RequestedUris);
        Assert.Equal(spooled.Select(record => record.EventId), harness.Receiver.Received.Select(envelope => envelope.EventId));
        Assert.Equal(lastSix, harness.SpooledFileNames());
        Assert.Empty(harness.DeadLetteredFileNames());
    }

    [Fact]
    public async Task DrainAsync_BatchMeetsATransientFailure_DeletesNothingAndStopsTheCycle()
    {
        using var harness = new SpoolDrainHarness(FakeAuditReceiver.Responding(HttpStatusCode.ServiceUnavailable), BatchesOf(64));
        await SpoolInOrderAsync(harness, 5);
        var spooledBefore = harness.SpooledFileNames();

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal([harness.BatchEndpoint], harness.Receiver.RequestedUris);
        Assert.Equal(spooledBefore, harness.SpooledFileNames());
        Assert.Empty(harness.DeadLetteredFileNames());
    }

    [Fact]
    public async Task DrainAsync_BatchRejected_RedeliversRecordByRecordAndDeadLettersOnlyTheOneRejectedOnItsOwn()
    {
        using var harness = new SpoolDrainHarness(
            FakeAuditReceiver.Responding(
                HttpStatusCode.UnprocessableEntity,
                HttpStatusCode.OK,
                HttpStatusCode.UnprocessableEntity,
                HttpStatusCode.OK),
            BatchesOf(64));
        await SpoolInOrderAsync(harness, 3);
        var rejectedOnItsOwn = harness.SpooledFileNames()[1];

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        // A rejected batch says one of its records is defective, not which: only the record the
        // endpoint rejects on its own is set aside (ADR 0020 D7).
        Assert.Equal(
            [harness.BatchEndpoint, harness.AuditEndpoint, harness.AuditEndpoint, harness.AuditEndpoint],
            harness.Receiver.RequestedUris);
        Assert.Equal([rejectedOnItsOwn], harness.DeadLetteredFileNames());
        Assert.Empty(harness.SpooledFileNames());
    }

    [Fact]
    public async Task DrainAsync_SeveralBatches_KeepsOldestFirstAcrossThemAndSendsALoneRecordToTheSingleEndpoint()
    {
        using var harness = new SpoolDrainHarness(FakeAuditReceiver.Responding(HttpStatusCode.OK), BatchesOf(2));
        var newest = await harness.SpoolAsync(AuditedOperation(Noon.AddMinutes(4)));
        var oldest = await harness.SpoolAsync(AuditedOperation(Noon));
        var third = await harness.SpoolAsync(AuditedOperation(Noon.AddMinutes(2)));
        var second = await harness.SpoolAsync(AuditedOperation(Noon.AddMinutes(1)));
        var fourth = await harness.SpoolAsync(AuditedOperation(Noon.AddMinutes(3)));

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [oldest.EventId, second.EventId, third.EventId, fourth.EventId, newest.EventId],
            harness.Receiver.Received.Select(envelope => envelope.EventId));
        Assert.Equal([harness.BatchEndpoint, harness.BatchEndpoint, harness.AuditEndpoint], harness.Receiver.RequestedUris);
        Assert.Empty(harness.SpooledFileNames());
    }

    [Fact]
    public async Task DrainAsync_BatchBody_IsTheStoredFilesJoinedIntoAJsonArray()
    {
        using var harness = new SpoolDrainHarness(FakeAuditReceiver.Responding(HttpStatusCode.OK), BatchesOf(64));
        await SpoolInOrderAsync(harness, 3);
        var stored = harness.SpooledFileNames()
            .Select(name => File.ReadAllBytes(Path.Combine(harness.SpoolOptions.SpoolDirectory, name)))
            .ToList();

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(harness.Receiver.Requests);
        byte[] expected = [(byte)'[', .. stored[0], (byte)',', .. stored[1], (byte)',', .. stored[2], (byte)']'];
        Assert.Equal(expected, request.Body);
        Assert.Equal("application/json; charset=utf-8", request.ContentType);
        Assert.Equal(expected.Length, request.ContentLength);
    }

    [Fact]
    public async Task DrainAsync_ConnectionDroppedAfterTheBatchWasStored_SendsTheWholeBatchAgainNextTime()
    {
        // The receiver has the records but this side never learns it, so every one of them stays
        // spooled and goes again; the receiver deduplicates on eventId (ADR 0020 D7).
        using var harness = new SpoolDrainHarness(
            new FakeAuditReceiver((requestIndex, _) => requestIndex == 0
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("the connection dropped after the batch was stored"))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))),
            BatchesOf(64));
        var spooled = (await SpoolInOrderAsync(harness, 3)).Select(record => record.EventId).ToList();

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, harness.SpooledFileNames().Count);

        await harness.StartedOver().DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal([harness.BatchEndpoint, harness.BatchEndpoint], harness.Receiver.RequestedUris);
        Assert.Equal([.. spooled, .. spooled], harness.Receiver.Received.Select(envelope => envelope.EventId));
        Assert.Empty(harness.SpooledFileNames());
        Assert.Empty(harness.DeadLetteredFileNames());
    }

    [Fact]
    public async Task DrainAsync_CorruptFileBetweenRecords_ClosesTheBatchBeforeItAndDeadLettersItOnItsOwn()
    {
        using var harness = new SpoolDrainHarness(FakeAuditReceiver.Responding(HttpStatusCode.OK), BatchesOf(64));
        var before = await harness.SpoolAsync(AuditedOperation(Noon));
        var corrupt = harness.PutInSpool("20260813T1200300000000Z-11111111-1111-7111-8111-111111111111.env", "not an envelope");
        var after = await harness.SpoolAsync(AuditedOperation(Noon.AddMinutes(1)));
        var last = await harness.SpoolAsync(AuditedOperation(Noon.AddMinutes(2)));

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal([harness.AuditEndpoint, harness.BatchEndpoint], harness.Receiver.RequestedUris);
        Assert.Equal([before.EventId, after.EventId, last.EventId], harness.Receiver.Received.Select(envelope => envelope.EventId));
        Assert.Equal([Path.GetFileName(corrupt)], harness.DeadLetteredFileNames());
        Assert.Empty(harness.SpooledFileNames());
    }

    [Fact]
    public async Task DrainAsync_DeliveredBatch_ReleasesItsRecordsFromTheTallyTheSinkWritesAgainst()
    {
        using var harness = new SpoolDrainHarness(FakeAuditReceiver.Responding(HttpStatusCode.OK), BatchesOf(64));
        await SpoolInOrderAsync(harness, 3);

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, harness.Tracker.Snapshot.Records);
        Assert.Equal(0, harness.Tracker.Snapshot.Bytes);
    }

    [Fact]
    public async Task DrainAsync_DeadLetteredCorruptFile_ReleasesItFromTheTally()
    {
        using var harness = new SpoolDrainHarness(FakeAuditReceiver.Responding(HttpStatusCode.OK), BatchesOf(64));
        harness.PutInSpool("20260813T1159000000000Z-11111111-1111-7111-8111-111111111111.env", "not an envelope");
        await harness.SpoolAsync(AuditedOperation(Noon));
        Assert.Equal(2, harness.Tracker.Snapshot.Records);

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, harness.Tracker.Snapshot.Records);
    }
}
