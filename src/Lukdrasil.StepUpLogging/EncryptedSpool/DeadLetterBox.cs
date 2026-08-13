using Microsoft.Extensions.Options;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// The <c>dead-letter/</c> directory beside the spool, holding the records the audit endpoint will
/// never accept and the spool files that can no longer be read. Nothing in this library ever
/// deletes from it: it is the evidence that an audit record never reached the audit store, and it
/// stays until an operator has looked at it (ADR 0020 D7).
/// </summary>
internal sealed class DeadLetterBox(IOptions<EncryptedSpoolOptions> options)
{
    /// <summary>The directory dead-lettered records are kept in, a sibling of the spool directory.</summary>
    public string DirectoryPath { get; } = SiblingOfSpool(options.Value.SpoolDirectory);

    /// <summary>True while any record is waiting to be looked at.</summary>
    public bool HoldsRecords => Directory.Exists(DirectoryPath) && Directory.EnumerateFiles(DirectoryPath).Any();

    /// <summary>
    /// Moves the spool file at <paramref name="spoolFilePath"/> in, keeping its name, and returns
    /// where it now is.
    /// </summary>
    public string Deposit(string spoolFilePath)
    {
        Directory.CreateDirectory(DirectoryPath);

        var destination = UnusedPathFor(Path.GetFileName(spoolFilePath));
        File.Move(spoolFilePath, destination);
        return destination;
    }

    private static string SiblingOfSpool(string spoolDirectory)
    {
        var spool = Path.GetFullPath(spoolDirectory);

        // A spool directly on a filesystem root has no sibling to sit beside; keeping the records
        // inside it is still better than dropping them, and readers ignore anything but *.env.
        return Path.Combine(Directory.GetParent(spool)?.FullName ?? spool, "dead-letter");
    }

    private string UnusedPathFor(string fileName)
    {
        // Delivery is at-least-once, so the same record can arrive here twice — a rejected record
        // whose spool file a crash left behind, redelivered and rejected again. The second copy
        // gets a name of its own rather than overwriting the evidence of the first.
        var path = Path.Combine(DirectoryPath, fileName);
        for (var duplicate = 1; File.Exists(path); duplicate++)
        {
            path = Path.Combine(
                DirectoryPath,
                $"{Path.GetFileNameWithoutExtension(fileName)}-{duplicate}{Path.GetExtension(fileName)}");
        }

        return path;
    }
}
