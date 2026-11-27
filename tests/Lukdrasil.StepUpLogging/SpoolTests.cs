using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;
using Microsoft.Extensions.Time.Testing;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Behaviour of <see cref="Spool"/> at its interface: what a reservation admits and writes, what
/// removing and setting aside do to the files and the tally, and which record the oldest age
/// follows while a drain reads (ADR 0041).
/// </summary>
public class SpoolTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    private static EncryptedSpoolOptions OptionsFor(TempSpoolDirectory root, int maxEntries = 10) => new()
    {
        // A spool below the temporary root, so its dead-letter sibling is thrown away with it.
        SpoolDirectory = Path.Combine(root.FullPath, "spool"),
        SpoolMaxEntries = maxEntries
    };

    private static async Task<Spool> SpoolHolding(TempSpoolDirectory root, params DateTimeOffset[] createdUtc)
    {
        var spool = new Spool(OptionsFor(root), new FakeTimeProvider());
        foreach (var instant in createdUtc)
        {
            await spool.Reserve(SpoolEnvelopes.CreatedAt(instant)).WriteAsync();
        }

        return spool;
    }

    private static string[] FilesIn(string directory) =>
        Directory.Exists(directory) ? [.. Directory.EnumerateFiles(directory).Select(Path.GetFileName).Order()!] : [];

    [Fact]
    public async Task Reserve_RoomLeft_WriteAsyncPutsTheRecordOnDiskAndCountsIt()
    {
        using var root = new TempSpoolDirectory();
        var options = OptionsFor(root);
        var spool = new Spool(options, new FakeTimeProvider());
        var envelope = SpoolEnvelopes.CreatedAt(Noon);

        var reservation = spool.Reserve(envelope);
        await reservation.WriteAsync();

        Assert.True(reservation.Verdict.Admitted);
        Assert.Equal([SpoolFile.NameFor(envelope)], FilesIn(options.SpoolDirectory));
        Assert.Equal(1, spool.Usage.Records);
        Assert.Equal(spool.Measure(), spool.Usage);
    }

    [Fact]
    public async Task Reserve_SpoolFull_RefusesAndTheReservationCannotBeWritten()
    {
        using var root = new TempSpoolDirectory();
        var options = OptionsFor(root, maxEntries: 1);
        var spool = new Spool(options, new FakeTimeProvider());
        await spool.Reserve(SpoolEnvelopes.CreatedAt(Noon)).WriteAsync();

        var reservation = spool.Reserve(SpoolEnvelopes.CreatedAt(Noon.AddSeconds(1)));

        Assert.False(reservation.Verdict.Admitted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reservation.WriteAsync());
        Assert.Single(FilesIn(options.SpoolDirectory));
        Assert.Equal(1, spool.Usage.Records);
    }

    [Fact]
    public async Task WriteAsync_WriteFails_RecordStaysCounted()
    {
        using var root = new TempSpoolDirectory();
        var options = OptionsFor(root);
        var failingWriter = new SpoolWriter(options.SpoolDirectory, (_, _) => Task.FromException(new IOException("disk gone")));
        var spool = new Spool(options, new FakeTimeProvider(), failingWriter);

        await Assert.ThrowsAsync<IOException>(() => spool.Reserve(SpoolEnvelopes.CreatedAt(Noon)).WriteAsync());

        Assert.Equal(1, spool.Usage.Records);
    }

    [Fact]
    public async Task Remove_DeliveredRecord_DeletesItsFileAndFreesItsRoom()
    {
        using var root = new TempSpoolDirectory();
        var spool = await SpoolHolding(root, Noon);

        foreach (var entry in spool.ReadOldestFirst())
        {
            spool.Remove(entry);
        }

        Assert.Empty(FilesIn(OptionsFor(root).SpoolDirectory));
        Assert.Equal(0, spool.Usage.Records);
        Assert.Equal(0, spool.Usage.Bytes);
    }

    [Fact]
    public async Task SetAside_ReadableRecord_MovesItToDeadLetterAndFreesItsRoom()
    {
        using var root = new TempSpoolDirectory();
        var spool = await SpoolHolding(root, Noon);
        var entry = spool.ReadOldestFirst().Single();

        var deadLetterPath = spool.SetAside(entry);

        Assert.Equal(Path.Combine(spool.DeadLetterDirectory, Path.GetFileName(entry.FilePath)), deadLetterPath);
        Assert.True(File.Exists(deadLetterPath));
        Assert.True(spool.HoldsDeadLetters);
        Assert.Empty(FilesIn(OptionsFor(root).SpoolDirectory));
        Assert.Equal(0, spool.Usage.Records);
        Assert.Equal(0, spool.Usage.Bytes);
    }

    [Fact]
    public async Task SetAside_UnreadableRecord_StaysCountedUntilMeasured()
    {
        using var root = new TempSpoolDirectory();
        var spool = await SpoolHolding(root, Noon);
        var path = Path.Combine(OptionsFor(root).SpoolDirectory, FilesIn(OptionsFor(root).SpoolDirectory)[0]);
        SpoolEntry entry;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            entry = spool.ReadOldestFirst().Single();
        }

        spool.SetAside(entry);

        Assert.True(entry.IsUnreadable);
        Assert.True(spool.HoldsDeadLetters);
        Assert.Equal(1, spool.Usage.Records);
        Assert.Equal(0, spool.Measure().Records);
    }

    [Fact]
    public async Task OldestRecordAgeAt_ReadInProgress_FollowsTheOldestRecordNotYetRemoved()
    {
        using var root = new TempSpoolDirectory();
        var spool = await SpoolHolding(root, Noon, Noon.AddSeconds(30));
        var now = Noon.AddSeconds(90);
        List<TimeSpan> ages = [];

        foreach (var entry in spool.ReadOldestFirst())
        {
            ages.Add(spool.OldestRecordAgeAt(now));
            spool.Remove(entry);
        }

        Assert.Equal([TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(60)], ages);
        Assert.Equal(TimeSpan.Zero, spool.OldestRecordAgeAt(now));
    }

    [Fact]
    public async Task OldestRecordAgeAt_RecordsReadAheadThenTheFirstRemoved_IsTheNextRecordsAge()
    {
        using var root = new TempSpoolDirectory();
        var spool = await SpoolHolding(root, Noon, Noon.AddSeconds(30));
        var now = Noon.AddSeconds(90);
        var read = spool.ReadOldestFirst().Take(2).ToList();

        spool.Remove(read[0]);

        Assert.Equal(TimeSpan.FromSeconds(60), spool.OldestRecordAgeAt(now));
    }

    [Fact]
    public async Task OldestRecordAgeAt_ReadStoppedOnARecordNotRemoved_KeepsThatRecordsAge()
    {
        using var root = new TempSpoolDirectory();
        var spool = await SpoolHolding(root, Noon, Noon.AddSeconds(30), Noon.AddSeconds(60));
        var now = Noon.AddSeconds(90);

        foreach (var entry in spool.ReadOldestFirst())
        {
            if (entry.CreatedUtc != Noon)
            {
                break;
            }

            spool.Remove(entry);
        }

        Assert.Equal(TimeSpan.FromSeconds(60), spool.OldestRecordAgeAt(now));
    }

    [Fact]
    public async Task OldestRecordAgeAt_LastRecordRemovedBeforeTheReadFoundNoMore_KeepsItsAgeUntilTheReadEnds()
    {
        using var root = new TempSpoolDirectory();
        var spool = await SpoolHolding(root, Noon);
        var now = Noon.AddSeconds(90);
        using var read = spool.ReadOldestFirst().GetEnumerator();
        Assert.True(read.MoveNext());

        spool.Remove(read.Current);
        var ageBeforeTheReadEnds = spool.OldestRecordAgeAt(now);
        Assert.False(read.MoveNext());

        Assert.Equal(TimeSpan.FromSeconds(90), ageBeforeTheReadEnds);
        Assert.Equal(TimeSpan.Zero, spool.OldestRecordAgeAt(now));
    }

    [Fact]
    public async Task OldestRecordAgeAt_EmptySpoolRead_IsZero()
    {
        using var root = new TempSpoolDirectory();
        var spool = await SpoolHolding(root);

        Assert.Empty(spool.ReadOldestFirst());

        Assert.Equal(TimeSpan.Zero, spool.OldestRecordAgeAt(Noon));
    }
}
