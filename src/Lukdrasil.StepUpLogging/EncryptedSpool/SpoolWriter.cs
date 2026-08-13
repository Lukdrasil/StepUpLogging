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
    /// Prepares the spool directory, deleting any <c>.tmp</c> file left behind by a crash
    /// mid-write: its record was never acknowledged to a caller, and its content may be truncated.
    /// </summary>
    public SpoolWriter(string spoolDirectory)
    {
        _spoolDirectory = spoolDirectory;
        Directory.CreateDirectory(spoolDirectory);
        DeleteOrphanedTemporaryFiles();
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

    private void DeleteOrphanedTemporaryFiles()
    {
        foreach (var orphan in Directory.EnumerateFiles(_spoolDirectory, $"*{SpoolFile.TemporaryExtension}"))
        {
            File.Delete(orphan);
        }
    }
}
