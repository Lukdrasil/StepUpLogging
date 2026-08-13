using System.Text.Json;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>One complete spool file: where it lives, and what it holds.</summary>
internal sealed record SpoolEntry(string FilePath, SpoolEnvelope Envelope);

/// <summary>
/// Reads the spool directory oldest first. Files still being written carry the <c>.tmp</c>
/// extension and are invisible here, so a record is only ever read whole (ADR 0020 D2).
/// </summary>
internal sealed class SpoolReader(string spoolDirectory)
{
    /// <summary>
    /// Returns the spooled records in creation order, oldest first — the order the file names sort
    /// in (ADR 0020 D3). Envelopes are deserialized lazily, one file at a time.
    /// </summary>
    public IEnumerable<SpoolEntry> ReadOldestFirst()
    {
        if (!Directory.Exists(spoolDirectory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(spoolDirectory, $"*{SpoolFile.EnvelopeExtension}")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Select(path => new SpoolEntry(path, ReadEnvelope(path)));
    }

    private static SpoolEnvelope ReadEnvelope(string path) =>
        JsonSerializer.Deserialize<SpoolEnvelope>(File.ReadAllBytes(path))
        ?? throw new InvalidDataException($"Spool file '{path}' does not contain an audit envelope.");
}
