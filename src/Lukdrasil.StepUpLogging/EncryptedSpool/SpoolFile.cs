using System.Globalization;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// The naming rule for spool files, shared by everything that reads or writes the spool
/// directory (ADR 0020 D3).
/// </summary>
internal static class SpoolFile
{
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
            $"{envelope.CreatedUtc.UtcDateTime:yyyyMMdd'T'HHmmssfffffff'Z'}-{envelope.EventId:D}{EnvelopeExtension}");

    /// <summary>The creation instant in the name of <paramref name="path"/>, or <see langword="null"/> for a foreign name.</summary>
    internal static DateTimeOffset? CreatedUtcOrNull(string path) => throw new NotImplementedException(); // af-stub
}
