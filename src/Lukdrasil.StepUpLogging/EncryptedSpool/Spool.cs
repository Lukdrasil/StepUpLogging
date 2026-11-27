namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// The spool directory as one module (ADR 0041). A record goes in through <see cref="Reserve"/>,
/// comes out oldest first through <see cref="ReadOldestFirst"/>, and leaves either delivered
/// (<see cref="Remove"/>) or set aside in <c>dead-letter/</c> (<see cref="SetAside"/>). The tally
/// the cap is judged on, the dead-letter directory and the oldest record still waiting are kept in
/// step with the files here, so no caller handles a release token or a head.
/// </summary>
/// <remarks>
/// Writers reserve and write concurrently. Reading, removing and setting aside belong to one drain
/// at a time: the oldest record waiting is worked out from the read in progress.
/// </remarks>
internal sealed class Spool
{
    private readonly SpoolWriter _writer;
    private readonly SpoolReader _reader;
    private readonly SpoolCapacity _capacity;
    private readonly SpoolUsageTracker _tracker;
    private readonly DeadLetterBox _deadLetters;
    private readonly SpoolHead _head = new();

    /// <summary>The entries the read in progress handed out and nothing has removed or set aside yet, oldest first.</summary>
    private readonly List<SpoolEntry> _unsettled = [];
    private bool _readToEnd;

    /// <summary>
    /// Opens the spool at <see cref="EncryptedSpoolOptions.SpoolDirectory"/>, creating it and
    /// recovering any <c>.tmp</c> a crash left behind (see <see cref="SpoolWriter"/>).
    /// </summary>
    public Spool(EncryptedSpoolOptions options, TimeProvider timeProvider)
        : this(options, timeProvider, new SpoolWriter(options.SpoolDirectory))
    {
    }

    /// <summary>As the public constructor, writing through <paramref name="writer"/>: the seam a test holds a write mid-flight with.</summary>
    internal Spool(EncryptedSpoolOptions options, TimeProvider timeProvider, SpoolWriter writer)
    {
        _writer = writer;
        _reader = new SpoolReader(options.SpoolDirectory);
        _capacity = new SpoolCapacity(options);
        _tracker = new SpoolUsageTracker(_capacity, timeProvider, options.SpoolFullRecheckInterval);
        _deadLetters = new DeadLetterBox(options);
    }

    /// <summary>What the spool holds from the tally, writes in flight included; no directory scan after the first.</summary>
    public SpoolUsage Usage => _tracker.Snapshot;

    /// <summary>The directory dead-lettered records are kept in, a sibling of the spool directory.</summary>
    public string DeadLetterDirectory => _deadLetters.DirectoryPath;

    /// <summary>True while any dead-lettered record is waiting to be looked at.</summary>
    public bool HoldsDeadLetters => _deadLetters.HoldsRecords;

    /// <summary>Reads what the spool directory holds right now, unlike <see cref="Usage"/>.</summary>
    public SpoolUsage Measure() => _capacity.Measure();

    /// <summary>
    /// How long the oldest record the drain is waiting on has waited at <paramref name="now"/>: the
    /// oldest entry the read in progress handed out and nothing removed or set aside. Zero once a
    /// read settled every entry it found, and for a creation instant after <paramref name="now"/>.
    /// </summary>
    public TimeSpan OldestRecordAgeAt(DateTimeOffset now) => _head.AgeAt(now);

    /// <summary>
    /// Serializes <paramref name="envelope"/> and asks for room for it. Only an admitted
    /// reservation may be written, and it counts against the cap until its write lands or fails.
    /// </summary>
    public SpoolReservation Reserve(SpoolEnvelope envelope)
    {
        var record = SpoolWriter.Prepare(envelope);
        return new(this, record, _tracker.TryReserve(record.Contents.Length));
    }

    /// <summary>
    /// The spooled records, oldest first (see <see cref="SpoolReader.ReadOldestFirst"/>). Each entry
    /// handed out is waited on until it is removed or set aside.
    /// </summary>
    public IEnumerable<SpoolEntry> ReadOldestFirst()
    {
        _unsettled.Clear();
        _readToEnd = false;

        foreach (var entry in _reader.ReadOldestFirst())
        {
            if (_unsettled.Count == 0)
            {
                _head.Seen(entry.CreatedUtc);
            }

            _unsettled.Add(entry);
            yield return entry;
        }

        _readToEnd = true;
        ClearHeadIfAllSettled();
    }

    /// <summary>Deletes a record the endpoint confirmed, and frees its room at once.</summary>
    public void Remove(SpoolEntry entry) =>
        TakeOut(entry, path =>
        {
            File.Delete(path);
            return path;
        });

    /// <summary>Moves a record no retry can deliver into <c>dead-letter/</c>, keeping its name, and returns where it now is.</summary>
    public string SetAside(SpoolEntry entry) => TakeOut(entry, _deadLetters.Deposit);

    /// <summary>Writes an admitted record and moves it from in flight onto the disk, or keeps it counted if the write fails.</summary>
    internal async Task WriteAsync(SpoolRecord record)
    {
        try
        {
            await _writer.WriteAsync(record).ConfigureAwait(false);
        }
        catch
        {
            // A write that failed part-way leaves a `.tmp` occupying the spool until the next
            // start-up sweep, never bigger than the record, so the record stays counted.
            _tracker.Abandon(record.Contents.Length);
            throw;
        }

        _tracker.Commit(record.Contents.Length);
    }

    private string TakeOut(SpoolEntry entry, Func<string, string> move)
    {
        // The token is taken before the file moves: a measure that runs in between may or may not
        // have seen the file, and then this release is ignored rather than counted off twice.
        var token = _tracker.ReleaseToken;
        var destination = move(entry.FilePath);

        // An unreadable file's size is unknown, so it stays counted until a measure finds it gone:
        // the tally may err high, never low.
        if (!entry.IsUnreadable)
        {
            _tracker.Released(token, entry.Contents.Length);
        }

        Settled(entry);
        return destination;
    }

    private void Settled(SpoolEntry entry)
    {
        _unsettled.Remove(entry);
        if (_unsettled.Count > 0)
        {
            _head.Seen(_unsettled[0].CreatedUtc);
            return;
        }

        ClearHeadIfAllSettled();
    }

    // Settling the last entry handed out clears the head only once the read found nothing more:
    // until then the next entry is still to come, and the head keeps the age the drain last waited on.
    private void ClearHeadIfAllSettled()
    {
        if (_readToEnd && _unsettled.Count == 0)
        {
            _head.Cleared();
        }
    }
}

/// <summary>Room asked for one record: the verdict, and for an admitted record the write that fills the room.</summary>
internal readonly struct SpoolReservation(Spool spool, SpoolRecord record, ReservationVerdict verdict)
{
    /// <summary>Whether the record was admitted, and the usage it was judged against.</summary>
    public ReservationVerdict Verdict => verdict;

    /// <summary>Writes the record and returns only once it is durably on disk and in place (ADR 0020 D1-D2).</summary>
    /// <exception cref="InvalidOperationException">The record was not admitted, so writing it would break the cap.</exception>
    public Task WriteAsync() =>
        verdict.Admitted
            ? spool.WriteAsync(record)
            : throw new InvalidOperationException("A record the spool refused room for cannot be written.");
}
