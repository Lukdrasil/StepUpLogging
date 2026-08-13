using System.Text.Json;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// One <c>.env</c> file found in the spool directory. <see cref="Envelope"/> is <see langword="null"/>
/// when the file exists but could not be read as a record — malformed JSON, most likely from a
/// disk fault, since the writer only ever renames a file whole. The caller dead-letters these
/// (<see cref="IsCorrupt"/>) rather than losing them silently.
/// </summary>
internal sealed record SpoolEntry(string FilePath, SpoolEnvelope? Envelope)
{
    /// <summary>True when the file at <see cref="FilePath"/> exists but is not a valid envelope.</summary>
    public bool IsCorrupt => Envelope is null;
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
    /// skipped with nothing left to act on. Every other read fault (malformed JSON, a locked file,
    /// a permission fault) leaves the file behind, still occupying cap, so it is yielded as a
    /// corrupt <see cref="SpoolEntry"/> rather than dropped, for the caller to dead-letter.
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
            // there, unlike the vanished case above, so it is corrupt rather than absent.
            return new SpoolEntry(path, Envelope: null);
        }

        try
        {
            return new SpoolEntry(path, JsonSerializer.Deserialize<SpoolEnvelope>(bytes));
        }
        catch (JsonException)
        {
            return new SpoolEntry(path, Envelope: null);
        }
    }
}
