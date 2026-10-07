using System.Text.Json;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>One spool record ready to write: the file name it is stored under and its exact bytes.</summary>
internal readonly record struct SpoolRecord(string FileName, byte[] Contents);

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
    private readonly Func<string, byte[], Task> _writeDurably;

    /// <summary>
    /// Prepares the spool directory and recovers any <c>.tmp</c> file left behind by a crash. Its
    /// bytes were already fsynced before the crash (<see cref="WriteAsync(SpoolEnvelope)"/>), so a <c>.tmp</c>
    /// that still parses as a whole envelope is a record that <em>was</em> acknowledged to a
    /// caller — only its rename into place did not survive — and is promoted to its <c>.env</c>
    /// name rather than discarded. Only a <c>.tmp</c> that fails to parse, i.e. one truncated
    /// mid-write, is deleted.
    /// </summary>
    public SpoolWriter(string spoolDirectory)
        : this(spoolDirectory, WriteDurablyAsync)
    {
    }

    /// <summary>
    /// As the public constructor, with the durable write replaced — the seam a test uses to hold a
    /// write mid-flight and prove that other writes do not wait for it.
    /// </summary>
    internal SpoolWriter(string spoolDirectory, Func<string, byte[], Task> writeDurably)
    {
        _spoolDirectory = spoolDirectory;
        _writeDurably = writeDurably;
        Directory.CreateDirectory(spoolDirectory);
        RecoverOrphanedTemporaryFiles();
    }

    /// <summary>
    /// Serializes <paramref name="envelope"/> into the bytes and file name it is spooled under, so
    /// the caller knows what the record will cost before it reserves room for it.
    /// </summary>
    internal static SpoolRecord Prepare(SpoolEnvelope envelope) =>
        new(SpoolFile.NameFor(envelope), JsonSerializer.SerializeToUtf8Bytes(envelope));

    /// <summary>Writes <paramref name="record"/> and returns only once it is durably on disk and in place.</summary>
    public async Task WriteAsync(SpoolRecord record)
    {
        var envelopePath = Path.Combine(_spoolDirectory, record.FileName);
        var temporaryPath = Path.ChangeExtension(envelopePath, SpoolFile.TemporaryExtension);

        await _writeDurably(temporaryPath, record.Contents).ConfigureAwait(false);

        // The rename is atomic on one volume, so a reader sees the record whole or not at all. The
        // directory entry itself is not fsynced — .NET has no portable API for that — so a power
        // loss right here can cost the rename, never the record's bytes (ADR 0020 D1): the next
        // start-up's recovery sweep re-attempts exactly this rename for a `.tmp` that survived.
        File.Move(temporaryPath, envelopePath);
    }

    /// <summary>
    /// Writes <paramref name="envelope"/> and returns only once it is durably on disk. There is
    /// deliberately no cancellation token: the caller's natural one would be
    /// <c>HttpContext.RequestAborted</c>, which would let a disconnecting client erase its own
    /// audit trail (ADR 0016 D6).
    /// </summary>
    /// <returns>
    /// The number of bytes the record now occupies in the spool, so a caller keeping the spool
    /// within a cap does not have to re-read the directory to learn what its own write cost.
    /// </returns>
    public async Task<long> WriteAsync(SpoolEnvelope envelope)
    {
        var record = Prepare(envelope);
        await WriteAsync(record).ConfigureAwait(false);
        return record.Contents.Length;
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
