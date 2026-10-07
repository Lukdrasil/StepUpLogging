using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Behaviour of <see cref="EncryptedSpoolAuditSink"/> under concurrent writers (issue #69): a
/// durable write no longer queues behind every other one, and a full spool reports its losses at
/// Critical without one log line per dropped record.
/// </summary>
public class EncryptedSpoolConcurrentWriteTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static EncryptedSpoolOptions OptionsFor(TempSpoolDirectory spool, int maxEntries = 100) => new()
    {
        SpoolDirectory = spool.FullPath,
        ModuleName = "orders-api",
        Version = "4.0.0",
        SpoolMaxEntries = maxEntries
    };

    private static EncryptedSpoolAuditSink CreateSink(
        EncryptedSpoolOptions options,
        SpoolWriter writer,
        ILogger<EncryptedSpoolAuditSink>? logger = null)
    {
        var time = new FakeTimeProvider();
        return new(
            Options.Create(options),
            writer,
            new SpoolUsageTracker(new SpoolCapacity(options), time, options.SpoolFullRecheckInterval),
            new FakeAuditPayloadEncryptor(),
            time,
            logger ?? NullLogger<EncryptedSpoolAuditSink>.Instance);
    }

    private static AuditEvent SpoolableEvent() =>
        AuditEvent.Success("order.cancel", "user-42", "user") with
        {
            EventId = Guid.CreateVersion7(),
            TimestampUtc = DateTimeOffset.UtcNow
        };

    private static async Task<bool> FinishesWithinPatience(Task task) =>
        await Task.WhenAny(task, Task.Delay(Patience, TestContext.Current.CancellationToken)) == task;

    [Fact]
    public async Task WriteAsync_SecondWriteWhileTheFirstIsHeldInItsDurableWrite_IsStoredWithoutWaitingForIt()
    {
        using var spool = new TempSpoolDirectory();
        var firstIsWriting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTheFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var durableWrites = 0;
        var writer = new SpoolWriter(spool.FullPath, async (path, contents) =>
        {
            if (Interlocked.Increment(ref durableWrites) == 1)
            {
                firstIsWriting.SetResult();
                await releaseTheFirst.Task;
            }

            await File.WriteAllBytesAsync(path, contents);
        });
        using var sink = CreateSink(OptionsFor(spool), writer);

        var first = sink.WriteAsync(SpoolableEvent()).AsTask();
        try
        {
            Assert.True(await FinishesWithinPatience(firstIsWriting.Task), "the first write never reached the durable-write seam");

            var second = sink.WriteAsync(SpoolableEvent()).AsTask();

            // The fsync dominates a write; one writer held in it must not hold every other one up.
            Assert.True(await FinishesWithinPatience(second), "the second write waited for the first one's fsync");
            Assert.Equal(AuditWriteResult.Stored, await second);
            Assert.False(first.IsCompleted);
        }
        finally
        {
            releaseTheFirst.TrySetResult();
        }

        Assert.Equal(AuditWriteResult.Stored, await first);
        Assert.Equal(2, Directory.GetFiles(spool.FullPath, "*.env").Length);
    }

    [Fact]
    public async Task WriteAsync_FiftyDropsAtTheCapWithinOneRecheckInterval_LogOneCritical()
    {
        using var spool = new TempSpoolDirectory();
        var logger = new RecordingLogger<EncryptedSpoolAuditSink>();
        using var sink = CreateSink(OptionsFor(spool, maxEntries: 1), new SpoolWriter(spool.FullPath), logger);
        Assert.Equal(AuditWriteResult.Stored, await sink.WriteAsync(SpoolableEvent()));

        var results = new List<AuditWriteResult>();
        for (var i = 0; i < 50; i++)
        {
            results.Add(await sink.WriteAsync(SpoolableEvent()));
        }

        Assert.All(results, result => Assert.Equal(AuditWriteResult.Dropped, result));
        Assert.Single(logger.MessagesAt(LogLevel.Critical));
    }
}
