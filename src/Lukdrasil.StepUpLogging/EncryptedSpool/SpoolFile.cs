using System.Globalization;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// The naming rule for spool files, shared by everything that reads or writes the spool
/// directory (ADR 0020 D3).
/// </summary>
internal static class SpoolFile
{
    private const string CreatedUtcFormat = "yyyyMMdd'T'HHmmssfffffff'Z'";

    /// <summary>The characters <see cref="CreatedUtcFormat"/> produces: the instant a file name starts with.</summary>
    private const int CreatedUtcLength = 23;

    /// <summary>Extension of a complete, readable spool file.</summary>
    internal const string EnvelopeExtension = ".env";

    /// <summary>Extension of a file still being written, invisible to readers.</summary>
    internal const string TemporaryExtension = ".tmp";

    /// <summary>
    /// The file name of <paramref name="envelope"/>: the creation instant first, so lexicographic
    /// order is chronological order, then the event id, so two records created in the same tick
    /// cannot collide.
    /// </summary>
    internal static string NameFor(SpoolEnvelope envelope) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{envelope.CreatedUtc.UtcDateTime.ToString(CreatedUtcFormat, CultureInfo.InvariantCulture)}-{envelope.EventId:D}{EnvelopeExtension}");

    /// <summary>The creation instant in the name of <paramref name="path"/>, or <see langword="null"/> for a foreign name.</summary>
    internal static DateTimeOffset? CreatedUtcOrNull(string path)
    {
        var name = Path.GetFileName(path.AsSpan());
        return name.Length >= CreatedUtcLength
            && DateTimeOffset.TryParseExact(
                name[..CreatedUtcLength],
                CreatedUtcFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var createdUtc)
                ? createdUtc
                : null;
    }
}
