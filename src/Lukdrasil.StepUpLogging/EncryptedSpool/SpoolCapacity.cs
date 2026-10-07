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
/// The answer to a request for room in the spool: whether the record was admitted, the usage it was
/// judged against, and whether this was the request that found the spool at half its cap.
/// </summary>
internal readonly record struct ReservationVerdict(bool Admitted, SpoolUsage Usage, bool CrossedWarnThreshold);

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
            return UsageOf(bytes: 0, records: 0);
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

        return UsageOf(bytes, records);
    }

    /// <summary>The usage of a spool holding <paramref name="records"/> records of <paramref name="bytes"/> bytes together.</summary>
    // Whichever cap is closest decides: a spool of many tiny records fills on the record count long
    // before the byte budget, and one of few large records the other way round.
    public SpoolUsage UsageOf(long bytes, int records) =>
        new(bytes, records, Math.Max((double)bytes / options.SpoolMaxBytes, (double)records / options.SpoolMaxEntries));
}

/// <summary>
/// The spool's fill level as the write path sees it: the disk stays the source of truth, but
/// re-reading the whole directory on every audit write — or on every dropped one, once the spool
/// is full — would put a scan proportional to the spool's depth on the business path.
/// </summary>
/// <remarks>
/// Writers reserve room before they write (<see cref="TryReserve"/>), so the cap holds for writers
/// in flight without any of them waiting for another's fsync. The tally is only ever too high,
/// never too low: records leave the spool behind this tracker's back — the drain worker deletes
/// them once delivered and says so through <see cref="Released"/>, which a measure may overtake —
/// so it can report the spool full early, but never late. Early would drop a record for room that
/// already exists, so a full verdict is confirmed against the disk, at most once per recheck
/// interval and by one thread at a time; every other writer at the cap is refused from the tally.
/// </remarks>
internal sealed class SpoolUsageTracker(SpoolCapacity capacity, TimeProvider timeProvider, TimeSpan fullRecheckInterval)
{
    private readonly object _gate = new();
    private readonly object _measureGate = new();
    private SpoolTally _tally;
    private long _measuredAt;
    private bool _wasAtWarnThreshold;
    private volatile bool _measuredOnce;

    /// <summary>
    /// The token a deleter takes before it deletes a record, to hand back to <see cref="Released"/>:
    /// a measure that ran in between may or may not have seen the file, so its release is ignored.
    /// </summary>
    public int ReleaseToken
    {
        get
        {
            lock (_gate)
            {
                return _tally.Generation;
            }
        }
    }

    /// <summary>
    /// Asks for room for one record of <paramref name="bytes"/>. A spool found full is measured again
    /// when the last measure is at least the recheck interval old, so room the drain freed shows up
    /// without a scan per dropped write.
    /// </summary>
    public ReservationVerdict TryReserve(long bytes)
    {
        EnsureMeasured();
        RecheckIfFullAndDue();

        lock (_gate)
        {
            var usage = _tally.Usage(capacity);
            var crossed = usage.IsAtWarnThreshold && !_wasAtWarnThreshold;
            _wasAtWarnThreshold = usage.IsAtWarnThreshold;

            if (usage.IsFull)
            {
                return new ReservationVerdict(Admitted: false, usage, crossed);
            }

            _tally = _tally.Reserve(bytes);
            return new ReservationVerdict(Admitted: true, usage, crossed);
        }
    }

    /// <summary>Moves a reserved record of <paramref name="bytes"/> onto the disk once its write finished.</summary>
    public void Commit(long bytes) => Update(tally => tally.Commit(bytes));

    /// <summary>Keeps counting a reserved record of <paramref name="bytes"/> whose write failed, since it may have left a <c>.tmp</c> behind.</summary>
    public void Abandon(long bytes) => Update(tally => tally.Abandon(bytes));

    /// <summary>Takes a delivered or dead-lettered record of <paramref name="bytes"/> off the tally, unless a measure overlapped it.</summary>
    public void Released(int token, long bytes) => Update(tally => tally.Release(bytes, token));

    /// <summary>True when a spool found full was last measured at least <paramref name="interval"/> ago.</summary>
    internal static bool NeedsMeasure(bool isFull, TimeSpan sinceMeasured, TimeSpan interval) =>
        isFull && sinceMeasured >= interval;

    /// <summary>
    /// The current usage, reservations in flight included, scanning disk only on the first call if
    /// nothing has measured yet (typically a restart sitting on a backlog no write in this process
    /// has touched). Cheap, frequent observation (an OTel gauge) never puts a directory enumeration
    /// on every collection interval. An idle-but-backlogged instance is exactly the case the gauge
    /// exists for, so the first call reports the disk rather than zero.
    /// </summary>
    public SpoolUsage Snapshot
    {
        get
        {
            EnsureMeasured();
            lock (_gate)
            {
                return _tally.Usage(capacity);
            }
        }
    }

    private void Update(Func<SpoolTally, SpoolTally> next)
    {
        lock (_gate)
        {
            _tally = next(_tally);
        }
    }

    private void EnsureMeasured()
    {
        if (_measuredOnce)
        {
            return;
        }

        // Concurrent first callers wait here for the one measure rather than each scanning.
        lock (_measureGate)
        {
            if (!_measuredOnce)
            {
                MeasureNow();
                _measuredOnce = true;
            }
        }
    }

    private void RecheckIfFullAndDue()
    {
        if (!IsFullAndDue() || !Monitor.TryEnter(_measureGate))
        {
            return;
        }

        // Whoever loses the race has nothing to wait for: the winner's result is on the tally
        // by the time they next ask, and meanwhile the tally's verdict only errs high.
        try
        {
            MeasureNow();
        }
        finally
        {
            Monitor.Exit(_measureGate);
        }
    }

    private bool IsFullAndDue()
    {
        lock (_gate)
        {
            return NeedsMeasure(_tally.Usage(capacity).IsFull, timeProvider.GetElapsedTime(_measuredAt), fullRecheckInterval);
        }
    }

    // The scan runs outside the gate — it is disk I/O, and writers must keep reserving meanwhile;
    // what they commit while it runs is carried by the tally, since the scan may have missed it.
    private void MeasureNow()
    {
        Update(tally => tally.BeginMeasure());
        var measured = capacity.Measure();
        lock (_gate)
        {
            _tally = _tally.EndMeasure(measured);
            _measuredAt = timeProvider.GetTimestamp();
        }
    }
}
