namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// How much of the spool's cap the records waiting for delivery take, and what that means: below
/// half the cap all is well, from half on the spool is reported unhealthy, and at the cap new
/// records are dropped (ADR 0020 D5-D6).
/// </summary>
internal readonly record struct SpoolUsage(long Bytes, int Records, double FillFraction)
{
    private const double WarnFraction = 0.5;

    /// <summary>True once the spool holds as much as its cap allows, so a new record is dropped.</summary>
    public bool IsFull => FillFraction >= 1;

    /// <summary>True from half the cap on, where writes still succeed but the spool is reported unhealthy.</summary>
    public bool IsAtWarnThreshold => FillFraction >= WarnFraction;
}

/// <summary>
/// Measures the spool directory against the caps configured for it. Only complete records count:
/// a <c>.tmp</c> file is a write in flight, and the writer that owns it is holding it for the
/// moment it takes to fsync and rename.
/// </summary>
internal sealed class SpoolCapacity(EncryptedSpoolOptions options)
{
    /// <summary>Reads what the spool directory holds right now.</summary>
    public SpoolUsage Measure()
    {
        // Counted off the disk on every call rather than tracked in memory: the drain worker and a
        // restart's recovery sweep add and remove spool files without telling anyone, so an
        // in-process counter would drift and end up rejecting writes into a spool that has room.
        var directory = new DirectoryInfo(options.SpoolDirectory);
        if (!directory.Exists)
        {
            return new SpoolUsage(Bytes: 0, Records: 0, FillFraction: 0);
        }

        long bytes = 0;
        var records = 0;
        foreach (var file in directory.EnumerateFiles($"*{SpoolFile.EnvelopeExtension}"))
        {
            bytes += file.Length;
            records++;
        }

        // Whichever cap is closest decides: a spool of many tiny records fills on the record count
        // long before the byte budget, and one of few large records the other way round.
        return new SpoolUsage(
            bytes,
            records,
            Math.Max((double)bytes / options.SpoolMaxBytes, (double)records / options.SpoolMaxEntries));
    }
}
