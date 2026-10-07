namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

// af-stub: red step for issue #69; the implementer replaces every member body.
internal readonly record struct SpoolTally
{
    public long DiskBytes { get; init; }

    public int DiskRecords { get; init; }

    public long ReservedBytes { get; init; }

    public int ReservedRecords { get; init; }

    public long LandedDuringMeasureBytes { get; init; }

    public int LandedDuringMeasureRecords { get; init; }

    public bool IsMeasuring { get; init; }

    public int Generation { get; init; }

    public SpoolTally Reserve(long bytes) => throw new NotImplementedException(); // af-stub

    public SpoolTally Commit(long bytes) => throw new NotImplementedException(); // af-stub

    public SpoolTally Abandon(long bytes) => throw new NotImplementedException(); // af-stub

    public SpoolTally Release(long bytes, int token) => throw new NotImplementedException(); // af-stub

    public SpoolTally BeginMeasure() => throw new NotImplementedException(); // af-stub

    public SpoolTally EndMeasure(SpoolUsage measured) => throw new NotImplementedException(); // af-stub

    public SpoolUsage Usage(SpoolCapacity capacity) => throw new NotImplementedException(); // af-stub
}
