using System.Net;
using System.Text;
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// The drain worker's pure decisions: how the spool, oldest first, is cut into deliveries
/// (<see cref="DrainWorker.Plan"/>), what a batch body is (<see cref="DrainWorker.BatchBody"/>),
/// and what the endpoint's answer means (<see cref="DrainWorker.Classify"/>, ADR 0020 D7).
/// </summary>
public class DrainPlanTests
{
    private static readonly Uri AuditEndpoint = new("https://audit.example/api/audit");

    private static SpoolEntry Readable(string name) =>
        new($"/spool/{name}.env", new SpoolEnvelope { EventId = Guid.CreateVersion7(), CreatedUtc = DateTimeOffset.UnixEpoch, Payload = [1] })
        {
            Contents = Encoding.UTF8.GetBytes($"{{\"name\":\"{name}\"}}")
        };

    private static SpoolEntry Faulted(string name, SpoolReadFault fault) => new($"/spool/{name}.env", Envelope: null, fault);

    private static IReadOnlyList<string> NamesIn(DrainWorker.DrainStep step) =>
        [.. step.Batch.Select(entry => Path.GetFileNameWithoutExtension(entry.FilePath))];

    /// <summary>A spool listing that fails if read past <paramref name="readable"/> entries.</summary>
    private static IEnumerable<SpoolEntry> SpoolThatMustNotBeReadPast(int readable)
    {
        for (var i = 0; i < readable; i++)
        {
            yield return Readable($"r{i}");
        }

        throw new InvalidOperationException("the plan read further into the spool than its first delivery needs");
    }

    [Fact]
    public void Plan_ReadableEntries_CutsThemIntoBatchesOfAtMostTheBatchSizeInOrder()
    {
        var steps = DrainWorker.Plan([Readable("a"), Readable("b"), Readable("c"), Readable("d"), Readable("e")], batchSize: 2).ToList();

        Assert.Equal([["a", "b"], ["c", "d"], ["e"]], steps.Select(NamesIn));
        Assert.All(steps, step => Assert.Null(step.Faulted));
    }

    [Fact]
    public void Plan_BatchSizeOne_DeliversEveryRecordOnItsOwn()
    {
        var steps = DrainWorker.Plan([Readable("a"), Readable("b"), Readable("c")], batchSize: 1).ToList();

        Assert.Equal([["a"], ["b"], ["c"]], steps.Select(NamesIn));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Plan_CorruptOrUnreadableEntry_ClosesTheOpenBatchAndStandsOnItsOwn(bool corrupt)
    {
        var faulted = Faulted("x", corrupt ? SpoolReadFault.Corrupt : SpoolReadFault.Unreadable);

        var steps = DrainWorker.Plan([Readable("a"), Readable("b"), faulted, Readable("c")], batchSize: 64).ToList();

        Assert.Equal(3, steps.Count);
        Assert.Equal(["a", "b"], NamesIn(steps[0]));
        Assert.Null(steps[0].Faulted);
        Assert.Same(faulted, steps[1].Faulted);
        Assert.Empty(steps[1].Batch);
        Assert.Equal(["c"], NamesIn(steps[2]));
    }

    [Fact]
    public void Plan_EmptySpool_PlansNothing()
    {
        Assert.Empty(DrainWorker.Plan([], batchSize: 64));
    }

    [Fact]
    public void Plan_FirstBatchFull_IsHandedOverBeforeTheRestOfTheSpoolIsRead()
    {
        // A delivery that stops the cycle (a transient failure) must not have paid for reading
        // the rest of a deep spool first.
        var first = DrainWorker.Plan(SpoolThatMustNotBeReadPast(2), batchSize: 2).First();

        Assert.Equal(["r0", "r1"], NamesIn(first));
    }

    [Fact]
    public void BatchBody_JoinsTheStoredBytesUnchangedIntoOneJsonArray()
    {
        var body = DrainWorker.BatchBody([Readable("a"), Readable("b")]);

        Assert.Equal("[{\"name\":\"a\"},{\"name\":\"b\"}]", Encoding.UTF8.GetString(body));
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, Uri? answeredFrom = null) =>
        new(status) { RequestMessage = new HttpRequestMessage(HttpMethod.Post, answeredFrom ?? AuditEndpoint) };

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.NoContent)]
    public void Classify_SuccessFromTheEndpointPostedTo_IsStored(HttpStatusCode status)
    {
        using var response = Answer(status);

        Assert.Equal(DrainWorker.DeliveryOutcome.Stored, DrainWorker.Classify(response, AuditEndpoint).Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(HttpStatusCode.UnsupportedMediaType)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public void Classify_StatusThatIndictsTheRecord_IsRejected(HttpStatusCode status)
    {
        using var response = Answer(status);

        Assert.Equal(DrainWorker.DeliveryOutcome.Rejected, DrainWorker.Classify(response, AuditEndpoint).Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void Classify_AnyOtherStatus_IsUndelivered(HttpStatusCode status)
    {
        using var response = Answer(status);

        Assert.Equal(DrainWorker.DeliveryOutcome.Undelivered, DrainWorker.Classify(response, AuditEndpoint).Outcome);
    }

    [Fact]
    public void Classify_SuccessFromAnotherUri_IsUndeliveredBecauseARedirectWasFollowed()
    {
        using var response = Answer(HttpStatusCode.OK, new Uri("https://audit.example/elsewhere"));

        var attempt = DrainWorker.Classify(response, AuditEndpoint);

        Assert.Equal(DrainWorker.DeliveryOutcome.Undelivered, attempt.Outcome);
        Assert.Contains("https://audit.example/elsewhere", attempt.Description);
    }
}
