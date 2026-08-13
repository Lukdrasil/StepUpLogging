using System.Text.Json;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// One <c>.env</c> file found in the spool directory. <see cref="Envelope"/> is
/// <see langword="null"/> if and only if <see cref="ReadFault"/> is not <see langword="null"/>, and
/// it says why: <see cref="SpoolReadFault.Corrupt"/> (malformed JSON, or valid JSON that is not an
/// envelope — the writer only ever renames a file whole, so this is a disk fault, not an in-flight
/// write) will read the same way on every attempt, but <see cref="SpoolReadFault.Unreadable"/> (a
/// sharing violation, a permission fault) may not — the same file may well be readable on the next
/// attempt. The caller dead-letters the former outright and the latter only after enough attempts
/// have failed, rather than losing either silently.
/// </summary>
internal sealed record SpoolEntry(string FilePath, SpoolEnvelope? Envelope, SpoolReadFault? ReadFault = null)
{
    /// <summary>True when <see cref="ReadFault"/> is <see cref="SpoolReadFault.Corrupt"/>.</summary>
    public bool IsCorrupt => ReadFault == SpoolReadFault.Corrupt;

    /// <summary>True when <see cref="ReadFault"/> is <see cref="SpoolReadFault.Unreadable"/>.</summary>
    public bool IsUnreadable => ReadFault == SpoolReadFault.Unreadable;
}

/// <summary>Why a <see cref="SpoolEntry"/> could not be read as an envelope.</summary>
internal enum SpoolReadFault
{
    /// <summary>The file exists but its content is not a valid envelope — malformed JSON.</summary>
    Corrupt,

    /// <summary>
    /// The file exists but could not be opened — a sharing violation, a permission fault, a disk
    /// read error. Not a defect in the record itself, so worth retrying, though not forever: a
    /// fault that never clears (a bad sector, a broken ACL) still has to be bounded by the caller.
    /// </summary>
    Unreadable
}

/// <summary>
/// Reads the spool directory oldest first. Files still being written carry the <c>.tmp</c>
/// extension and are invisible here, so a record is only ever read whole (ADR 0020 D2).
/// </summary>
internal sealed class SpoolReader(string spoolDirectory)
{
    /// <summary>
    /// Returns the spooled records in creation order, oldest first — the order the file names sort
    /// in (ADR 0020 D3). Envelopes are deserialized lazily, one file at a time. A file that vanishes
    /// between the directory listing and the read — another drainer deleted it concurrently — is
    /// skipped with nothing left to act on. Every other read fault leaves the file behind, still
    /// occupying cap, so it is yielded as a faulted <see cref="SpoolEntry"/> rather than dropped,
    /// carrying which kind of fault it was for the caller to act on.
    /// </summary>
    public IEnumerable<SpoolEntry> ReadOldestFirst()
    {
        if (!Directory.Exists(spoolDirectory))
        {
            yield break;
        }

        var paths = Directory
            .EnumerateFiles(spoolDirectory, $"*{SpoolFile.EnvelopeExtension}")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal);

        foreach (var path in paths)
        {
            var entry = ReadEntryOrNull(path);
            if (entry is not null)
            {
                yield return entry;
            }
        }
    }

    private static SpoolEntry? ReadEntryOrNull(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception)
        {
            // A sharing violation, a permission fault, a disk read error — the file is still
            // there, unlike the vanished case above, but nothing here says the record itself is
            // bad, so it is unreadable rather than corrupt.
            return new SpoolEntry(path, Envelope: null, SpoolReadFault.Unreadable);
        }

        SpoolEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SpoolEnvelope>(bytes);
        }
        catch (JsonException)
        {
            return new SpoolEntry(path, Envelope: null, SpoolReadFault.Corrupt);
        }

        // Valid JSON that deserializes to null (the literal `null`) throws nothing above, but is
        // exactly as permanently unreadable as malformed JSON: this content will never parse into
        // an envelope on a later attempt either, so it must not be classified as a recoverable
        // fault (keeps "Envelope is null implies ReadFault is not null" total for callers).
        return envelope is null
            ? new SpoolEntry(path, Envelope: null, SpoolReadFault.Corrupt)
            : new SpoolEntry(path, envelope);
    }
}
