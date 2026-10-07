using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Behaviour of <see cref="SpoolTally"/>: what the spool holds on disk and in flight, and the one
/// direction it may be wrong in — too high, never too low, so the cap never lets a record through
/// that the disk has no room for (issue #69).
/// </summary>
public class SpoolTallyTests
{
    private static readonly SpoolTally ThreeOnDisk = new() { DiskBytes = 300, DiskRecords = 3 };

    [Fact]
    public void Reserve_CountsTheRecordAsInFlightNotYetOnDisk()
    {
        var tally = new SpoolTally().Reserve(100);

        Assert.Equal(100, tally.ReservedBytes);
        Assert.Equal(1, tally.ReservedRecords);
        Assert.Equal(0, tally.DiskBytes);
        Assert.Equal(0, tally.DiskRecords);
    }

    [Fact]
    public void Commit_MovesTheReservationOntoTheDisk()
    {
        var tally = new SpoolTally().Reserve(100).Commit(100);

        Assert.Equal(100, tally.DiskBytes);
        Assert.Equal(1, tally.DiskRecords);
        Assert.Equal(0, tally.ReservedBytes);
        Assert.Equal(0, tally.ReservedRecords);
    }

    [Fact]
    public void Abandon_CountsTheFailedWriteAsLandedSoTheTallyOnlyErrsHigh()
    {
        // A failed write can leave a .tmp behind, never bigger than the record it was writing.
        var tally = new SpoolTally().Reserve(100).Abandon(100);

        Assert.Equal(100, tally.DiskBytes);
        Assert.Equal(1, tally.DiskRecords);
        Assert.Equal(0, tally.ReservedRecords);
    }

    [Fact]
    public void Release_WithTheCurrentGeneration_TakesTheRecordOffTheDisk()
    {
        var tally = ThreeOnDisk.Release(100, ThreeOnDisk.Generation);

        Assert.Equal(200, tally.DiskBytes);
        Assert.Equal(2, tally.DiskRecords);
    }

    [Fact]
    public void Release_WithATokenTakenBeforeAMeasureBegan_IsIgnored()
    {
        var token = ThreeOnDisk.Generation;

        var tally = ThreeOnDisk.BeginMeasure().Release(100, token);

        // The measure may already have missed the deleted file, so counting it off again would undercount.
        Assert.Equal(300, tally.DiskBytes);
        Assert.Equal(3, tally.DiskRecords);
    }

    [Fact]
    public void Release_WithATokenTakenDuringAMeasure_IsIgnoredOnceItEnded()
    {
        var measuring = ThreeOnDisk.BeginMeasure();
        var token = measuring.Generation;

        var tally = measuring.EndMeasure(new SpoolUsage(200, 2, 0)).Release(100, token);

        Assert.Equal(200, tally.DiskBytes);
        Assert.Equal(2, tally.DiskRecords);
    }

    [Fact]
    public void Release_MoreThanTheDiskHolds_StopsAtZero()
    {
        var tally = new SpoolTally { DiskBytes = 50, DiskRecords = 0 }.Release(100, 0);

        Assert.Equal(0, tally.DiskBytes);
        Assert.Equal(0, tally.DiskRecords);
    }

    [Fact]
    public void EndMeasure_SetsTheDiskToTheMeasureAndAddsWhatLandedWhileItRan()
    {
        var tally = ThreeOnDisk
            .Reserve(100)
            .BeginMeasure()
            .Commit(100)
            .EndMeasure(new SpoolUsage(200, 2, 0));

        Assert.Equal(300, tally.DiskBytes);
        Assert.Equal(3, tally.DiskRecords);
        Assert.False(tally.IsMeasuring);
    }

    [Fact]
    public void EndMeasure_KeepsTheReservationsStillInFlight()
    {
        var tally = new SpoolTally()
            .Reserve(100)
            .BeginMeasure()
            .EndMeasure(new SpoolUsage(0, 0, 0));

        Assert.Equal(100, tally.ReservedBytes);
        Assert.Equal(1, tally.ReservedRecords);
    }

    [Fact]
    public void BeginMeasure_ForgetsWhatLandedBeforeIt()
    {
        var tally = new SpoolTally()
            .Reserve(100)
            .Commit(100)
            .BeginMeasure()
            .EndMeasure(new SpoolUsage(100, 1, 0));

        Assert.Equal(100, tally.DiskBytes);
        Assert.Equal(1, tally.DiskRecords);
    }

    [Fact]
    public void BeginMeasureAndEndMeasure_EachStartANewGeneration()
    {
        var measuring = ThreeOnDisk.BeginMeasure();
        var measured = measuring.EndMeasure(new SpoolUsage(300, 3, 0));

        Assert.True(measuring.IsMeasuring);
        Assert.NotEqual(ThreeOnDisk.Generation, measuring.Generation);
        Assert.NotEqual(measuring.Generation, measured.Generation);
    }

    [Fact]
    public void Usage_CountsTheDiskAndTheReservationsAgainstTheCap()
    {
        var capacity = new SpoolCapacity(new EncryptedSpoolOptions { SpoolMaxEntries = 10, SpoolMaxBytes = 10_000 });

        var usage = ThreeOnDisk.Reserve(100).Usage(capacity);

        Assert.Equal(400, usage.Bytes);
        Assert.Equal(4, usage.Records);
        Assert.Equal(0.4, usage.FillFraction, precision: 10);
    }
}
