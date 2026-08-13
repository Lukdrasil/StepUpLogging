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
/// Measures the spool directory against the caps configured for it.
/// </summary>
internal sealed class SpoolCapacity(EncryptedSpoolOptions options)
{
    /// <summary>Reads what the spool directory holds right now.</summary>
    public SpoolUsage Measure()
    {
        var directory = new DirectoryInfo(options.SpoolDirectory);
        if (!directory.Exists)
        {
            return Usage(bytes: 0, records: 0);
        }

        long bytes = 0;
        var records = 0;

        // Every file here occupies the spool, a `.tmp` included: a write that failed after creating
        // one leaves it behind until the next start-up sweep, and bytes nothing counts are bytes the
        // cap cannot bound.
        foreach (var file in directory.EnumerateFiles())
        {
            bytes += file.Length;
            records++;
        }

        return Usage(bytes, records);
    }

    /// <summary>The usage <paramref name="usage"/> becomes once one more record of <paramref name="recordBytes"/> is spooled.</summary>
    public SpoolUsage Grown(SpoolUsage usage, long recordBytes) => Usage(usage.Bytes + recordBytes, usage.Records + 1);

    // Whichever cap is closest decides: a spool of many tiny records fills on the record count long
    // before the byte budget, and one of few large records the other way round.
    private SpoolUsage Usage(long bytes, int records) =>
        new(bytes, records, Math.Max((double)bytes / options.SpoolMaxBytes, (double)records / options.SpoolMaxEntries));
}

/// <summary>
/// The spool's fill level as the write path sees it: the disk stays the source of truth, but
/// re-reading the whole directory on every audit write would put a scan proportional to the spool's
/// depth on the business path — tens of milliseconds once a stalled receiver has let it grow.
/// </summary>
/// <remarks>
/// The write path still owns correctness — the sink reads and updates this under the gate that
/// serializes its writes, so only ever one thread updates the tally. The lock exists for
/// <see cref="Snapshot"/> alone: a gauge observes the tally from a collection thread, and every
/// update takes the lock so that observation can never catch a half-written value.
/// </remarks>
internal sealed class SpoolUsageTracker(SpoolCapacity capacity)
{
    private readonly object _gate = new();
    private SpoolUsage? _addedUp;

    /// <summary>
    /// The current usage, exact wherever being wrong would cost a record. Records only ever leave
    /// the spool behind this tracker's back — the drain worker deletes them once delivered — so an
    /// added-up value is only ever too high, never too low: it can report the spool full early, but
    /// never late. Early is what would drop a record for room that already exists, so that one
    /// verdict is confirmed against the disk before it is returned.
    /// </summary>
    public SpoolUsage Read()
    {
        var usage = _addedUp ?? capacity.Measure();
        if (usage.IsFull)
        {
            usage = capacity.Measure();
        }

        lock (_gate)
        {
            _addedUp = usage;
        }

        return usage;
    }

    /// <summary>Adds a record of <paramref name="recordBytes"/> that reached the spool.</summary>
    public void Recorded(long recordBytes)
    {
        lock (_gate)
        {
            if (_addedUp is { } usage)
            {
                _addedUp = capacity.Grown(usage, recordBytes);
            }
        }
    }

    /// <summary>Drops what was added up, so the next read comes from the disk again.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _addedUp = null;
        }
    }

    /// <summary>
    /// The tally as it stands, scanning disk at most once — the first call, if nothing has primed it
    /// yet (typically a restart sitting on a backlog no write in this process has touched). Every
    /// later call reuses that tally instead of re-scanning, so cheap, frequent observation (an OTel
    /// gauge) never puts a directory enumeration on every collection interval. Same tolerance as
    /// <see cref="Read"/>: only ever too high, never too low — reporting zero here as long as the
    /// spool sat unread would break that, since an idle-but-backlogged instance is exactly the case
    /// the gauge exists for.
    /// </summary>
    public SpoolUsage Snapshot
    {
        get
        {
            lock (_gate)
            {
                if (_addedUp is { } usage)
                {
                    return usage;
                }
            }

            // The scan happens outside the lock, matching Read(): it is disk I/O, and nothing else
            // needs the lock held across it.
            var measured = capacity.Measure();
            lock (_gate)
            {
                _addedUp ??= measured;
                return _addedUp.Value;
            }
        }
    }
}
