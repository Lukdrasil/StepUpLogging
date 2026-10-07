using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;
using Microsoft.Extensions.Time.Testing;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Behaviour of <see cref="SpoolUsageTracker"/> over a real spool directory: the cap holds for
/// writers in flight, and a full spool is confirmed against the disk at most once per recheck
/// interval instead of on every write (issue #69).
/// </summary>
public class SpoolUsageTrackerTests
{
    private const long RecordBytes = 10;

    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(1);

    private static SpoolUsageTracker TrackerFor(TempSpoolDirectory spool, int maxEntries, FakeTimeProvider? time = null) =>
        new(
            new SpoolCapacity(new EncryptedSpoolOptions { SpoolDirectory = spool.FullPath, SpoolMaxEntries = maxEntries }),
            time ?? new FakeTimeProvider(),
            RecheckInterval);

    private static string PutRecordOnDisk(TempSpoolDirectory spool)
    {
        var path = Path.Combine(spool.FullPath, $"{Guid.NewGuid():N}.env");
        File.WriteAllBytes(path, new byte[RecordBytes]);
        return path;
    }

    [Fact]
    public void TryReserve_RoomLeft_AdmitsAndCountsTheRecordInFlight()
    {
        using var spool = new TempSpoolDirectory();
        var tracker = TrackerFor(spool, maxEntries: 2);

        var verdict = tracker.TryReserve(RecordBytes);

        Assert.True(verdict.Admitted);
        Assert.Equal(1, tracker.Snapshot.Records);
        Assert.Equal(RecordBytes, tracker.Snapshot.Bytes);
    }

    [Fact]
    public void TryReserve_ReservationsInFlightFillTheCap_RefusesTheNextBeforeAnyOfThemReachedTheDisk()
    {
        using var spool = new TempSpoolDirectory();
        var tracker = TrackerFor(spool, maxEntries: 2);

        Assert.True(tracker.TryReserve(RecordBytes).Admitted);
        Assert.True(tracker.TryReserve(RecordBytes).Admitted);

        var verdict = tracker.TryReserve(RecordBytes);

        Assert.False(verdict.Admitted);
        Assert.True(verdict.Usage.IsFull);
    }

    [Fact]
    public void TryReserve_FirstUse_CountsWhatAnEarlierRunLeftOnDisk()
    {
        using var spool = new TempSpoolDirectory();
        PutRecordOnDisk(spool);
        PutRecordOnDisk(spool);
        var tracker = TrackerFor(spool, maxEntries: 3);

        Assert.True(tracker.TryReserve(RecordBytes).Admitted);
        Assert.False(tracker.TryReserve(RecordBytes).Admitted);
    }

    [Fact]
    public void TryReserve_FullWithinTheRecheckInterval_KeepsRefusingWithoutScanningTheDiskAgain()
    {
        using var spool = new TempSpoolDirectory();
        var time = new FakeTimeProvider();
        var onDisk = PutRecordOnDisk(spool);
        var tracker = TrackerFor(spool, maxEntries: 1, time);
        Assert.False(tracker.TryReserve(RecordBytes).Admitted);

        File.Delete(onDisk);
        time.Advance(RecheckInterval / 2);

        // A scan per dropped write is what made a full spool expensive: the freed room shows up
        // on the next recheck, not on every write.
        Assert.False(tracker.TryReserve(RecordBytes).Admitted);
    }

    [Fact]
    public void TryReserve_FullOnceTheRecheckIntervalPassed_MeasuresAgainAndAdmitsIntoTheFreedRoom()
    {
        using var spool = new TempSpoolDirectory();
        var time = new FakeTimeProvider();
        var onDisk = PutRecordOnDisk(spool);
        var tracker = TrackerFor(spool, maxEntries: 1, time);
        Assert.False(tracker.TryReserve(RecordBytes).Admitted);

        File.Delete(onDisk);
        time.Advance(RecheckInterval);

        Assert.True(tracker.TryReserve(RecordBytes).Admitted);
    }

    [Fact]
    public void TryReserve_ReachingHalfTheCap_ReportsTheCrossingOnce()
    {
        using var spool = new TempSpoolDirectory();
        var tracker = TrackerFor(spool, maxEntries: 4);

        var crossings = Enumerable.Range(0, 4).Select(_ => tracker.TryReserve(RecordBytes).CrossedWarnThreshold).ToArray();

        // Judged on the usage before each reservation: the third write is the first to find the spool half full.
        Assert.Equal([false, false, true, false], crossings);
    }

    [Fact]
    public void Abandon_KeepsCountingTheFailedWriteAgainstTheCap()
    {
        using var spool = new TempSpoolDirectory();
        var tracker = TrackerFor(spool, maxEntries: 2);
        Assert.True(tracker.TryReserve(RecordBytes).Admitted);

        tracker.Abandon(RecordBytes);

        Assert.True(tracker.TryReserve(RecordBytes).Admitted);
        Assert.False(tracker.TryReserve(RecordBytes).Admitted);
    }

    [Fact]
    public void Released_WithTheCurrentToken_FreesTheRoomWithoutWaitingForARecheck()
    {
        using var spool = new TempSpoolDirectory();
        var tracker = TrackerFor(spool, maxEntries: 1);
        Assert.True(tracker.TryReserve(RecordBytes).Admitted);
        tracker.Commit(RecordBytes);
        Assert.False(tracker.TryReserve(RecordBytes).Admitted);

        tracker.Released(tracker.ReleaseToken, RecordBytes);

        Assert.True(tracker.TryReserve(RecordBytes).Admitted);
    }

    [Fact]
    public void Commit_MovesTheRecordFromInFlightToTheDiskWithoutChangingTheSnapshot()
    {
        using var spool = new TempSpoolDirectory();
        var tracker = TrackerFor(spool, maxEntries: 4);
        tracker.TryReserve(RecordBytes);

        tracker.Commit(RecordBytes);

        Assert.Equal(1, tracker.Snapshot.Records);
        Assert.Equal(RecordBytes, tracker.Snapshot.Bytes);
    }

    [Theory]
    [InlineData(false, 5000)]
    [InlineData(true, 0)]
    [InlineData(true, 999)]
    public void NeedsMeasure_NotFullOrCheckedWithinTheInterval_IsFalse(bool isFull, int sinceMeasuredMilliseconds)
    {
        Assert.False(SpoolUsageTracker.NeedsMeasure(isFull, TimeSpan.FromMilliseconds(sinceMeasuredMilliseconds), RecheckInterval));
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(5000)]
    public void NeedsMeasure_FullAndCheckedAtLeastAnIntervalAgo_IsTrue(int sinceMeasuredMilliseconds)
    {
        Assert.True(SpoolUsageTracker.NeedsMeasure(true, TimeSpan.FromMilliseconds(sinceMeasuredMilliseconds), RecheckInterval));
    }
}
