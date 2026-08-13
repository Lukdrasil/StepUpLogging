using System.Text.Json;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// Writes one <see cref="SpoolEnvelope"/> per file into the spool directory: to a
/// <c>.tmp</c> file, flushed to the device, then atomically renamed into place, so a reader only
/// ever sees complete records and a crash mid-write cannot leave a truncated one (ADR 0020 D1-D2).
/// </summary>
/// <remarks>
/// Thread-safe: the writer holds no mutable state, and every record has a file name of its own.
/// </remarks>
internal sealed class SpoolWriter
{
    private readonly string _spoolDirectory;

    /// <summary>
    /// Prepares the spool directory and recovers any <c>.tmp</c> file left behind by a crash. Its
    /// bytes were already fsynced before the crash (<see cref="WriteAsync"/>), so a <c>.tmp</c>
    /// that still parses as a whole envelope is a record that <em>was</em> acknowledged to a
    /// caller — only its rename into place did not survive — and is promoted to its <c>.env</c>
    /// name rather than discarded. Only a <c>.tmp</c> that fails to parse, i.e. one truncated
    /// mid-write, is deleted.
    /// </summary>
    public SpoolWriter(string spoolDirectory)
    {
        _spoolDirectory = spoolDirectory;
        Directory.CreateDirectory(spoolDirectory);
        RecoverOrphanedTemporaryFiles();
    }

    /// <summary>
    /// Writes <paramref name="envelope"/> and returns only once it is durably on disk. There is
    /// deliberately no cancellation token: the caller's natural one would be
    /// <c>HttpContext.RequestAborted</c>, which would let a disconnecting client erase its own
    /// audit trail (ADR 0016 D6).
    /// </summary>
    public async Task WriteAsync(SpoolEnvelope envelope)
    {
        var envelopePath = Path.Combine(_spoolDirectory, SpoolFile.NameFor(envelope));
        var temporaryPath = Path.ChangeExtension(envelopePath, SpoolFile.TemporaryExtension);

        await WriteDurablyAsync(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(envelope));

        // The rename is atomic on one volume, so a reader sees the record whole or not at all. The
        // directory entry itself is not fsynced — .NET has no portable API for that — so a power
        // loss right here can cost the rename, never the record's bytes (ADR 0020 D1): the next
        // start-up's recovery sweep re-attempts exactly this rename for a `.tmp` that survived.
        File.Move(temporaryPath, envelopePath);
    }

    private static async Task WriteDurablyAsync(string path, byte[] contents)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous
        };

        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(contents);

        // The record has to reach the device, not just the OS page cache: a power loss after
        // WriteAsync returned would otherwise lose a record the caller was told was durable
        // (ADR 0020 D1). This fsync is what makes an audit write cost milliseconds.
        stream.Flush(flushToDisk: true);
    }

    private void RecoverOrphanedTemporaryFiles()
    {
        foreach (var orphan in Directory.EnumerateFiles(_spoolDirectory, $"*{SpoolFile.TemporaryExtension}"))
        {
            try
            {
                RecoverOrphan(orphan);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover this instance cannot move or delete (a peer writer's in-flight
                // `.tmp`, an AV lock, a permissions issue) must not take the host down at
                // start-up; it is picked up again on the next restart.
            }
        }
    }

    private void RecoverOrphan(string temporaryPath)
    {
        SpoolEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SpoolEnvelope>(File.ReadAllBytes(temporaryPath));
        }
        catch (JsonException)
        {
            envelope = null;
        }

        if (envelope is null)
        {
            File.Delete(temporaryPath);
            return;
        }

        File.Move(temporaryPath, Path.Combine(_spoolDirectory, SpoolFile.NameFor(envelope)));
    }
}
