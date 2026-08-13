using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

namespace Lukdrasil.StepUpLogging.Tests;

public class SpoolCrashRestartTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Write_Succeeds_LeavesOneCompleteEnvelopeFileAndNoTemporaryFile()
    {
        using var spool = new TempSpoolDirectory();

        await new SpoolWriter(spool.FullPath).WriteAsync(GoldenSpoolEnvelope.Create());

        Assert.Empty(Directory.GetFiles(spool.FullPath, "*.tmp"));
        var envelopeFile = Assert.Single(Directory.GetFiles(spool.FullPath, "*.env"));
        Assert.Equal(GoldenSpoolEnvelope.Json, File.ReadAllText(envelopeFile));
    }

    [Fact]
    public async Task Write_RenameBlocked_LeavesTheCompleteBytesInATemporaryFileAndNoEnvelopeFile()
    {
        using var spool = new TempSpoolDirectory();

        // A directory occupying the envelope's path makes the rename — and only the rename — fail,
        // which is what exposes where the bytes were written before it.
        Directory.CreateDirectory(Path.Combine(spool.FullPath, GoldenSpoolEnvelope.FileName));

        await Assert.ThrowsAnyAsync<IOException>(
            () => new SpoolWriter(spool.FullPath).WriteAsync(GoldenSpoolEnvelope.Create()));

        Assert.Empty(Directory.GetFiles(spool.FullPath, "*.env"));
        var temporaryFile = Assert.Single(Directory.GetFiles(spool.FullPath, "*.tmp"));
        Assert.Equal(GoldenSpoolEnvelope.Json, File.ReadAllText(temporaryFile));
    }

    [Fact]
    public async Task Restart_AfterACrashMidWrite_DeletesTheOrphanedTemporaryFileAndKeepsEverySpooledRecord()
    {
        using var spool = new TempSpoolDirectory();
        var spooled = new[] { SpoolEnvelopes.CreatedAt(Noon), SpoolEnvelopes.CreatedAt(Noon.AddSeconds(1)) };
        var crashedWriter = new SpoolWriter(spool.FullPath);
        foreach (var envelope in spooled)
        {
            await crashedWriter.WriteAsync(envelope);
        }

        File.WriteAllText(
            Path.Combine(spool.FullPath, "20260813T1200020000000Z-truncated.tmp"),
            GoldenSpoolEnvelope.Json[..40]);

        _ = new SpoolWriter(spool.FullPath);

        Assert.Empty(Directory.GetFiles(spool.FullPath, "*.tmp"));
        Assert.Equal(
            spooled.Select(envelope => envelope.EventId),
            new SpoolReader(spool.FullPath).ReadOldestFirst().Select(entry => entry.Envelope!.EventId));
    }

    [Fact]
    public void Restart_AfterACrashRightAfterFsyncButBeforeTheRename_PromotesTheCompleteOrphanToAReadableRecord()
    {
        using var spool = new TempSpoolDirectory();
        var envelope = GoldenSpoolEnvelope.Create();
        var orphanPath = Path.ChangeExtension(
            Path.Combine(spool.FullPath, GoldenSpoolEnvelope.FileName), SpoolFile.TemporaryExtension);
        File.WriteAllText(orphanPath, GoldenSpoolEnvelope.Json);

        _ = new SpoolWriter(spool.FullPath);

        Assert.Empty(Directory.GetFiles(spool.FullPath, "*.tmp"));
        var entry = Assert.Single(new SpoolReader(spool.FullPath).ReadOldestFirst());
        Assert.False(entry.IsCorrupt);
        Assert.Equal(envelope.EventId, entry.Envelope!.EventId);
        Assert.Equal(GoldenSpoolEnvelope.FileName, Path.GetFileName(entry.FilePath));
    }

    [Fact]
    public async Task Write_SpoolDirectoryDoesNotExistYet_CreatesItAndSpoolsTheRecord()
    {
        using var spool = new TempSpoolDirectory();
        var spoolDirectory = Path.Combine(spool.FullPath, "audit-spool");

        await new SpoolWriter(spoolDirectory).WriteAsync(GoldenSpoolEnvelope.Create());

        Assert.Equal(GoldenSpoolEnvelope.FileName, Path.GetFileName(Assert.Single(Directory.GetFiles(spoolDirectory))));
    }
}
