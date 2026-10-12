using System.Net;
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// The rejection observer (issue #71): after the drain worker has dead-lettered a record the
/// endpoint rejected on its own, each registered <see cref="IAuditRejectionObserver"/> is told the
/// status and problem code. It informs only: the outcome is the same with or without it, and a
/// slow or failing observer never stops the cycle.
/// </summary>
public class DrainWorkerRejectionObserverTests
{
    private const string UnknownKey = "sealing.unknown_key";

    private static readonly DateTimeOffset Noon = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    private static readonly Func<HttpResponseMessage> UnknownKeyRejection =
        FakeAuditReceiver.Problem(HttpStatusCode.UnprocessableEntity, $$"""{"status":422,"code":"{{UnknownKey}}"}""");

    private static AuditEvent AuditedOperation(DateTimeOffset at) =>
        AuditEvent.Success("order.cancel", "user-42", "user") with
        {
            EventId = Guid.CreateVersion7(),
            TimestampUtc = at
        };

    private static async Task SpoolInOrderAsync(SpoolDrainHarness harness, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await harness.SpoolAsync(AuditedOperation(Noon.AddSeconds(i)));
        }
    }

    [Fact]
    public async Task RejectedWithProblemCode_NotifiesObserverWithStatusAndCode()
    {
        var observer = new RecordingRejectionObserver();
        using var harness = new SpoolDrainHarness(FakeAuditReceiver.Answering(UnknownKeyRejection), rejectionObservers: [observer]);
        await SpoolInOrderAsync(harness, 1);

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new AuditRejection(422, UnknownKey), Assert.Single(observer.Rejections));
    }

    [Fact]
    public async Task RejectedWithObserver_StillDeadLettersAndDeliversNext()
    {
        var observer = new RecordingRejectionObserver();
        using var harness = new SpoolDrainHarness(
            FakeAuditReceiver.Answering(UnknownKeyRejection, FakeAuditReceiver.Stored()),
            rejectionObservers: [observer]);
        await SpoolInOrderAsync(harness, 2);

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Single(observer.Rejections);
        Assert.Single(harness.DeadLetteredFileNames());
        Assert.Empty(harness.SpooledFileNames());
        Assert.Equal(2, harness.Receiver.Received.Count);
        Assert.Contains(UnknownKey, Assert.Single(harness.Logger.MessagesAt(LogLevel.Critical)));
    }

    [Fact]
    public async Task RejectedWithoutObserver_DeadLetterLogOmitsProblemCode()
    {
        using var harness = new SpoolDrainHarness(FakeAuditReceiver.Answering(UnknownKeyRejection));
        await SpoolInOrderAsync(harness, 1);

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Single(harness.DeadLetteredFileNames());
        Assert.DoesNotContain("problem code", Assert.Single(harness.Logger.MessagesAt(LogLevel.Critical)));
    }

    [Fact]
    public async Task FirstObserverThrows_SecondNotified_NextRecordDelivered()
    {
        var throwing = new RecordingRejectionObserver((_, _) => throw new InvalidOperationException("key store unreachable"));
        var second = new RecordingRejectionObserver();
        using var harness = new SpoolDrainHarness(
            FakeAuditReceiver.Answering(UnknownKeyRejection, FakeAuditReceiver.Stored()),
            rejectionObservers: [throwing, second]);
        await SpoolInOrderAsync(harness, 2);

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Single(second.Rejections);
        Assert.Equal(2, harness.Receiver.Received.Count);
        Assert.Empty(harness.SpooledFileNames());
        Assert.Contains(nameof(RecordingRejectionObserver), Assert.Single(harness.Logger.MessagesAt(LogLevel.Warning)));
    }

    [Fact]
    public async Task ObserverNeverCompletes_GivesUpAfterDeliveryTimeout()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hanging = new RecordingRejectionObserver(async (_, cancellationToken) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        });
        using var harness = new SpoolDrainHarness(
            FakeAuditReceiver.Answering(UnknownKeyRejection, FakeAuditReceiver.Stored()),
            rejectionObservers: [hanging]);
        await SpoolInOrderAsync(harness, 2);

        var drain = harness.Worker.DrainAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(drain.IsCompleted);

        harness.Time.Advance(harness.SpoolOptions.DeliveryTimeout);
        await drain.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(2, harness.Receiver.Received.Count);
        Assert.Single(harness.Logger.MessagesAt(LogLevel.Warning));
    }

    [Fact]
    public async Task RejectedBatch_NotifiesOnlyForRecordRejectedAlone()
    {
        var observer = new RecordingRejectionObserver();
        using var harness = new SpoolDrainHarness(
            FakeAuditReceiver.Answering(
                FakeAuditReceiver.Problem(HttpStatusCode.UnprocessableEntity, """{"code":"batch.defective"}"""),
                FakeAuditReceiver.Stored(),
                UnknownKeyRejection,
                FakeAuditReceiver.Stored()),
            options => options.DeliveryBatchSize = 64,
            [observer]);
        await SpoolInOrderAsync(harness, 3);

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new AuditRejection(422, UnknownKey), Assert.Single(observer.Rejections));
        Assert.Single(harness.DeadLetteredFileNames());
    }

    [Fact]
    public async Task RejectionWithPlainTextBody_NotifiesWithNullCode()
    {
        var observer = new RecordingRejectionObserver();
        using var harness = new SpoolDrainHarness(
            FakeAuditReceiver.Answering(FakeAuditReceiver.Problem(HttpStatusCode.UnprocessableEntity, "unprocessable", "text/plain")),
            rejectionObservers: [observer]);
        await SpoolInOrderAsync(harness, 1);

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new AuditRejection(422, null), Assert.Single(observer.Rejections));
    }

    [Fact]
    public async Task RejectionWithUnreadableCode_StillDeadLettersAndNotifiesWithNullCode()
    {
        var observer = new RecordingRejectionObserver();
        using var harness = new SpoolDrainHarness(
            FakeAuditReceiver.Answering(
                FakeAuditReceiver.Problem(HttpStatusCode.UnprocessableEntity, """{"code":"\uD800"}"""),
                FakeAuditReceiver.Stored()),
            rejectionObservers: [observer]);
        await SpoolInOrderAsync(harness, 2);

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new AuditRejection(422, null), Assert.Single(observer.Rejections));
        Assert.Single(harness.DeadLetteredFileNames());
        Assert.Empty(harness.SpooledFileNames());
        Assert.Equal(2, harness.Receiver.Received.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task StoredOrTransient_NeverNotifies(HttpStatusCode status)
    {
        var observer = new RecordingRejectionObserver();
        using var harness = new SpoolDrainHarness(
            FakeAuditReceiver.Answering(FakeAuditReceiver.Problem(status, $$"""{"code":"{{UnknownKey}}"}""")),
            rejectionObservers: [observer]);
        await SpoolInOrderAsync(harness, 1);

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Empty(observer.Rejections);
    }

    [Fact]
    public async Task AddEncryptedSpoolAuditSink_RegisteredObserver_IsNotified()
    {
        using var spool = new TempSpoolDirectory();
        var observer = new RecordingRejectionObserver();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IAuditPayloadEncryptor, FakeAuditPayloadEncryptor>();
        builder.Services.AddSingleton<IAuditRejectionObserver>(observer);
        builder.AddEncryptedSpoolAuditSink(options =>
        {
            options.SpoolDirectory = Path.Combine(spool.FullPath, "spool");
            options.EndpointBaseUrl = "https://audit.example/api";
            options.ModuleName = "orders-api";
            options.Version = "4.0.0";
        });
        builder.Services.AddHttpClient(DrainWorker.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => FakeAuditReceiver.Answering(UnknownKeyRejection));
        using var host = builder.Build();
        await host.Services.GetRequiredService<SpoolWriter>().WriteAsync(SpoolEnvelopes.CreatedAt(DateTimeOffset.UtcNow));

        await host.Services.GetServices<IHostedService>().OfType<DrainWorker>().Single()
            .DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new AuditRejection(422, UnknownKey), Assert.Single(observer.Rejections));
    }
}
