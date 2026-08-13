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

        Assert.Equal(written.EventId, entry.Envelope.EventId);
        Assert.Equal(written.CreatedUtc, entry.Envelope.CreatedUtc);
        Assert.Equal(written.Payload, entry.Envelope.Payload);
        Assert.Equal(GoldenSpoolEnvelope.FileName, Path.GetFileName(entry.FilePath));
    }

    [Fact]
    public async Task ReadOldestFirst_RecordsWrittenOutOfChronologicalOrder_ReturnsThemOldestFirst()
    {
        using var spool = new TempSpoolDirectory();
        var writer = new SpoolWriter(spool.FullPath);

        await writer.WriteAsync(EnvelopeCreatedAt(Noon.AddSeconds(30)));
        await writer.WriteAsync(EnvelopeCreatedAt(Noon));
        await writer.WriteAsync(EnvelopeCreatedAt(Noon.AddTicks(1)));

        var entries = new SpoolReader(spool.FullPath).ReadOldestFirst().ToList();

        Assert.Equal(
            [Noon, Noon.AddTicks(1), Noon.AddSeconds(30)],
            entries.Select(entry => entry.Envelope.CreatedUtc));
    }

    [Fact]
    public async Task ReadOldestFirst_TwoRecordsCreatedInTheSameTick_ReturnsBothOnDistinctFiles()
    {
        using var spool = new TempSpoolDirectory();
        var writer = new SpoolWriter(spool.FullPath);
        var first = EnvelopeCreatedAt(Noon);
        var second = EnvelopeCreatedAt(Noon);

        await writer.WriteAsync(first);
        await writer.WriteAsync(second);

        var entries = new SpoolReader(spool.FullPath).ReadOldestFirst().ToList();
        Assert.Equal(2, Directory.GetFiles(spool.FullPath).Length);
        Assert.Equal(
            new[] { first.EventId, second.EventId }.Order(),
            entries.Select(entry => entry.Envelope.EventId).Order());
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

    private static SpoolEnvelope EnvelopeCreatedAt(DateTimeOffset createdUtc) => new()
    {
        EventId = Guid.CreateVersion7(createdUtc),
        CreatedUtc = createdUtc,
        Payload = [0x2a]
    };
}
