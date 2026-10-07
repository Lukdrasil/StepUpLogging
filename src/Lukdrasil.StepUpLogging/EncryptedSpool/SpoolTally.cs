namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// What the spool holds as the write path adds it up: records on the disk, and records reserved
/// but still being written. A pure value — every operation returns the next tally — so the rules
/// that keep the cap exact are testable without a directory or a thread (issue #69).
/// </summary>
/// <remarks>
/// The tally may be wrong in one direction only, too high: a record it counts that is already gone
/// can report the spool full early, never late. A scan of the directory (<see cref="BeginMeasure"/>
/// to <see cref="EndMeasure"/>) overlaps writes and deletes that the scan may or may not have seen,
/// so each of them starts a new <see cref="Generation"/> and a <see cref="Release"/> that was
/// taken against an older one is ignored rather than risk counting a file off twice.
/// </remarks>
internal readonly record struct SpoolTally
{
    /// <summary>Bytes of the records on the disk.</summary>
    public long DiskBytes { get; init; }

    /// <summary>Records on the disk.</summary>
    public int DiskRecords { get; init; }

    /// <summary>Bytes of the records reserved and not yet on the disk.</summary>
    public long ReservedBytes { get; init; }

    /// <summary>Records reserved and not yet on the disk.</summary>
    public int ReservedRecords { get; init; }

    /// <summary>Bytes that reached the disk while a measure ran, which the measure may have missed.</summary>
    public long LandedDuringMeasureBytes { get; init; }

    /// <summary>Records that reached the disk while a measure ran, which the measure may have missed.</summary>
    public int LandedDuringMeasureRecords { get; init; }

    /// <summary>True between <see cref="BeginMeasure"/> and <see cref="EndMeasure"/>.</summary>
    public bool IsMeasuring { get; init; }

    /// <summary>Changes whenever a measure begins or ends; a release is only valid in the generation it was taken in.</summary>
    public int Generation { get; init; }

    /// <summary>Counts a record of <paramref name="bytes"/> as being written.</summary>
    public SpoolTally Reserve(long bytes) =>
        this with { ReservedBytes = ReservedBytes + bytes, ReservedRecords = ReservedRecords + 1 };

    /// <summary>Moves a reserved record of <paramref name="bytes"/> onto the disk, and into what a running measure may have missed.</summary>
    public SpoolTally Commit(long bytes) =>
        this with
        {
            ReservedBytes = Math.Max(0, ReservedBytes - bytes),
            ReservedRecords = Math.Max(0, ReservedRecords - 1),
            DiskBytes = DiskBytes + bytes,
            DiskRecords = DiskRecords + 1,
            LandedDuringMeasureBytes = LandedDuringMeasureBytes + (IsMeasuring ? bytes : 0),
            LandedDuringMeasureRecords = LandedDuringMeasureRecords + (IsMeasuring ? 1 : 0)
        };

    /// <summary>
    /// Counts a reserved record whose write failed as landed. A failed write can leave a <c>.tmp</c>
    /// behind, never bigger than the record, so counting the whole record errs high only.
    /// </summary>
    public SpoolTally Abandon(long bytes) => Commit(bytes);

    /// <summary>Takes a delivered record of <paramref name="bytes"/> off the disk, if <paramref name="token"/> is still the current generation.</summary>
    public SpoolTally Release(long bytes, int token) =>
        token != Generation
            ? this
            : this with { DiskBytes = Math.Max(0, DiskBytes - bytes), DiskRecords = Math.Max(0, DiskRecords - 1) };

    /// <summary>Starts a measure: what landed before it is on the disk the measure will see.</summary>
    public SpoolTally BeginMeasure() =>
        this with
        {
            IsMeasuring = true,
            Generation = Generation + 1,
            LandedDuringMeasureBytes = 0,
            LandedDuringMeasureRecords = 0
        };

    /// <summary>Ends a measure: the disk is what it found plus what landed while it ran; reservations stay.</summary>
    public SpoolTally EndMeasure(SpoolUsage measured) =>
        this with
        {
            IsMeasuring = false,
            Generation = Generation + 1,
            DiskBytes = measured.Bytes + LandedDuringMeasureBytes,
            DiskRecords = measured.Records + LandedDuringMeasureRecords,
            LandedDuringMeasureBytes = 0,
            LandedDuringMeasureRecords = 0
        };

    /// <summary>The usage against the caps of <paramref name="capacity"/>, the reservations in flight included.</summary>
    public SpoolUsage Usage(SpoolCapacity capacity) =>
        capacity.UsageOf(DiskBytes + ReservedBytes, DiskRecords + ReservedRecords);
}
