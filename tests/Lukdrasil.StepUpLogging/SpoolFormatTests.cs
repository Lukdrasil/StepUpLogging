using System.Globalization;
using System.Text;
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

namespace Lukdrasil.StepUpLogging.Tests;

public class SpoolFormatTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GoldenEnvelope_WrittenToSpool_MatchesPinnedFileNameAndBytes()
    {
        using var spool = new TempSpoolDirectory();

        await new SpoolWriter(spool.FullPath).WriteAsync(GoldenSpoolEnvelope.Create());

        var file = Assert.Single(Directory.GetFiles(spool.FullPath));
        Assert.Equal(GoldenSpoolEnvelope.FileName, Path.GetFileName(file));
        Assert.Equal(GoldenSpoolEnvelope.Json, Encoding.UTF8.GetString(File.ReadAllBytes(file)));
    }

    [Fact]
    public async Task GoldenEnvelope_ReadBackFromSpool_RoundTripsEveryFieldUnchanged()
    {
        using var spool = new TempSpoolDirectory();
        var written = GoldenSpoolEnvelope.Create();

        await new SpoolWriter(spool.FullPath).WriteAsync(written);
        var entry = Assert.Single(new SpoolReader(spool.FullPath).ReadOldestFirst());

        Assert.False(entry.IsCorrupt);
        Assert.Equal(written.EventId, entry.Envelope!.EventId);
        Assert.Equal(written.CreatedUtc, entry.Envelope.CreatedUtc);
        Assert.Equal(written.Payload, entry.Envelope.Payload);
        Assert.Equal(GoldenSpoolEnvelope.FileName, Path.GetFileName(entry.FilePath));
    }

    [Fact]
    public async Task ReadOldestFirst_RecordsWrittenOutOfChronologicalOrder_ReturnsThemOldestFirst()
    {
        using var spool = new TempSpoolDirectory();
        var writer = new SpoolWriter(spool.FullPath);

        await writer.WriteAsync(SpoolEnvelopes.CreatedAt(Noon.AddSeconds(30)));
        await writer.WriteAsync(SpoolEnvelopes.CreatedAt(Noon));
        await writer.WriteAsync(SpoolEnvelopes.CreatedAt(Noon.AddTicks(1)));

        var entries = new SpoolReader(spool.FullPath).ReadOldestFirst().ToList();

        Assert.Equal(
            [Noon, Noon.AddTicks(1), Noon.AddSeconds(30)],
            entries.Select(entry => entry.Envelope!.CreatedUtc));
    }

    [Fact]
    public async Task ReadOldestFirst_TwoRecordsCreatedInTheSameTick_ReturnsBothOnDistinctFiles()
    {
        using var spool = new TempSpoolDirectory();
        var writer = new SpoolWriter(spool.FullPath);
        var first = SpoolEnvelopes.CreatedAt(Noon);
        var second = SpoolEnvelopes.CreatedAt(Noon);

        await writer.WriteAsync(first);
        await writer.WriteAsync(second);

        var entries = new SpoolReader(spool.FullPath).ReadOldestFirst().ToList();
        Assert.Equal(2, Directory.GetFiles(spool.FullPath).Length);
        Assert.Equal(
            new[] { first.EventId, second.EventId }.Order(),
            entries.Select(entry => entry.Envelope!.EventId).Order());
    }

    [Fact]
    public async Task ReadOldestFirst_TemporaryFileAlongsideACompleteOne_ReturnsOnlyTheCompleteRecord()
    {
        using var spool = new TempSpoolDirectory();
        await new SpoolWriter(spool.FullPath).WriteAsync(GoldenSpoolEnvelope.Create());
        File.WriteAllText(Path.Combine(spool.FullPath, "20260813T1200000000000Z-in-flight.tmp"), GoldenSpoolEnvelope.Json);

        var entry = Assert.Single(new SpoolReader(spool.FullPath).ReadOldestFirst());

        Assert.Equal(GoldenSpoolEnvelope.FileName, Path.GetFileName(entry.FilePath));
    }

    [Fact]
    public void ReadOldestFirst_SpoolDirectoryDoesNotExist_ReturnsNoRecords()
    {
        using var spool = new TempSpoolDirectory();

        var reader = new SpoolReader(Path.Combine(spool.FullPath, "never-written"));

        Assert.Empty(reader.ReadOldestFirst());
    }

    [Fact]
    public async Task ReadOldestFirst_OneEnvelopeFileIsCorrupt_SkipsItButReturnsTheGoodOnesAndExposesTheCorruptPath()
    {
        using var spool = new TempSpoolDirectory();
        var writer = new SpoolWriter(spool.FullPath);
        var before = SpoolEnvelopes.CreatedAt(Noon);
        var after = SpoolEnvelopes.CreatedAt(Noon.AddSeconds(1));
        await writer.WriteAsync(before);
        await writer.WriteAsync(after);
        var corruptPath = Path.Combine(spool.FullPath, "20260813T1200003000000Z-corrupt.env");
        File.WriteAllText(corruptPath, "{not json");

        var entries = new SpoolReader(spool.FullPath).ReadOldestFirst().ToList();

        Assert.Equal(
            [before.EventId, after.EventId],
            entries.Where(entry => !entry.IsCorrupt).Select(entry => entry.Envelope!.EventId));
        var corrupt = Assert.Single(entries, entry => entry.IsCorrupt);
        Assert.Equal(corruptPath, corrupt.FilePath);
    }

    [Fact]
    public async Task ReadOldestFirst_OneEnvelopeFileContainsTheJsonLiteralNull_ClassifiesAsCorruptNotUnreadable()
    {
        using var spool = new TempSpoolDirectory();
        await new SpoolWriter(spool.FullPath).WriteAsync(GoldenSpoolEnvelope.Create());
        // Valid JSON that deserializes to a null envelope without throwing: unlike a locked file or
        // a permission fault, this content will never read differently, so it must dead-letter
        // rather than stall a drain worker retrying it forever.
        var nullPath = Path.Combine(spool.FullPath, "20260813T1200003000000Z-11111111-1111-7111-8111-111111111111.env");
        File.WriteAllText(nullPath, "null");

        var entries = new SpoolReader(spool.FullPath).ReadOldestFirst().ToList();

        var nullEntry = Assert.Single(entries, entry => entry.FilePath == nullPath);
        Assert.True(nullEntry.IsCorrupt);
        Assert.False(nullEntry.IsUnreadable);
        Assert.Null(nullEntry.Envelope);
    }

    [Fact]
    public async Task ReadOldestFirst_AFileVanishesAfterTheDirectoryIsListedButBeforeItIsOpened_SkipsItAndReturnsTheRest()
    {
        using var spool = new TempSpoolDirectory();
        var writer = new SpoolWriter(spool.FullPath);
        var first = SpoolEnvelopes.CreatedAt(Noon);
        var vanishing = SpoolEnvelopes.CreatedAt(Noon.AddSeconds(1));
        var last = SpoolEnvelopes.CreatedAt(Noon.AddSeconds(2));
        await writer.WriteAsync(first);
        await writer.WriteAsync(vanishing);
        await writer.WriteAsync(last);
        var vanishingPath = Path.Combine(spool.FullPath, SpoolFile.NameFor(vanishing));

        // A second drainer deleting `vanishing` right after the directory is listed, but before
        // this enumeration reaches it, is exactly the race the reader has to tolerate.
        using var entries = new SpoolReader(spool.FullPath).ReadOldestFirst().GetEnumerator();
        Assert.True(entries.MoveNext());
        Assert.Equal(first.EventId, entries.Current.Envelope!.EventId);
        File.Delete(vanishingPath);

        Assert.True(entries.MoveNext());
        Assert.Equal(last.EventId, entries.Current.Envelope!.EventId);
        Assert.False(entries.MoveNext());
    }

    [Fact]
    public async Task ReadOldestFirst_AFileIsLockedWhenOpened_SurfacesAsUnreadableAndEnumerationContinues()
    {
        using var spool = new TempSpoolDirectory();
        var writer = new SpoolWriter(spool.FullPath);
        var first = SpoolEnvelopes.CreatedAt(Noon);
        var locked = SpoolEnvelopes.CreatedAt(Noon.AddSeconds(1));
        var last = SpoolEnvelopes.CreatedAt(Noon.AddSeconds(2));
        await writer.WriteAsync(first);
        await writer.WriteAsync(locked);
        await writer.WriteAsync(last);
        var lockedPath = Path.Combine(spool.FullPath, SpoolFile.NameFor(locked));

        using var entries = new SpoolReader(spool.FullPath).ReadOldestFirst().GetEnumerator();
        Assert.True(entries.MoveNext());
        Assert.Equal(first.EventId, entries.Current.Envelope!.EventId);

        // The file exists but a read fault other than "vanished" (here, an exclusive lock held
        // by another process) still leaves it on disk: it must surface as unreadable, not
        // corrupt and not silently dropped, so B08 retries it instead of dead-lettering it.
        using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(entries.MoveNext());
            Assert.True(entries.Current.IsUnreadable);
            Assert.False(entries.Current.IsCorrupt);
            Assert.Equal(lockedPath, entries.Current.FilePath);
        }

        Assert.True(entries.MoveNext());
        Assert.Equal(last.EventId, entries.Current.Envelope!.EventId);
        Assert.False(entries.MoveNext());
    }

    [Fact]
    public async Task ReadOldestFirst_RecordsAtCloseInstantsWithDifferentOffsets_OrdersByUtcInstantNotWallClock()
    {
        using var spool = new TempSpoolDirectory();
        var writer = new SpoolWriter(spool.FullPath);
        var zeroOffset = SpoolEnvelopes.CreatedAt(new DateTimeOffset(2026, 8, 13, 14, 0, 0, TimeSpan.Zero));
        // One minute earlier in UTC than zeroOffset, but its own wall clock (15:59) reads later:
        // comparing wall clock instead of the UTC instant would flip the expected order below.
        var earlierUtcLaterWallClock = SpoolEnvelopes.CreatedAt(new DateTimeOffset(2026, 8, 13, 15, 59, 0, TimeSpan.FromHours(2)));

        await writer.WriteAsync(zeroOffset);
        await writer.WriteAsync(earlierUtcLaterWallClock);

        var entries = new SpoolReader(spool.FullPath).ReadOldestFirst().ToList();

        Assert.Equal(
            [earlierUtcLaterWallClock.EventId, zeroOffset.EventId],
            entries.Select(entry => entry.Envelope!.EventId));
    }

    [Fact]
    public void NameFor_UnderANonGregorianCurrentCulture_StillProducesTheInvariantCultureFileName()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("th-TH");
        try
        {
            Assert.Equal(GoldenSpoolEnvelope.FileName, SpoolFile.NameFor(GoldenSpoolEnvelope.Create()));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
