namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// The creation instant of the oldest record the drain worker is waiting on, so how long a stalled
/// drain has been stalled can be read without scanning the spool. The drain worker writes it; the
/// <c>audit_spool_oldest_age_seconds</c> gauge and the health check read it from other threads.
/// </summary>
internal sealed class SpoolHead
{
    private const long Empty = -1;

    // A plain long read and written through Interlocked: a long store is not atomic on 32-bit
    // platforms, and C# does not allow a volatile long.
    private long _createdUtcTicks = Empty;

    /// <summary>The worker is about to deliver the record created at <paramref name="createdUtc"/>; <see langword="null"/> when that instant is unknown.</summary>
    public void Seen(DateTimeOffset? createdUtc) =>
        Interlocked.Exchange(ref _createdUtcTicks, createdUtc?.UtcTicks ?? Empty);

    /// <summary>The spool holds no record the worker is waiting on.</summary>
    public void Cleared() => Interlocked.Exchange(ref _createdUtcTicks, Empty);

    /// <summary>How long the oldest record has waited at <paramref name="now"/>; zero for an empty spool and for a creation instant after <paramref name="now"/> (clock skew).</summary>
    public TimeSpan AgeAt(DateTimeOffset now)
    {
        var ticks = Interlocked.Read(ref _createdUtcTicks);
        return ticks == Empty || ticks > now.UtcTicks ? TimeSpan.Zero : TimeSpan.FromTicks(now.UtcTicks - ticks);
    }

    /// <summary>The age in whole seconds, the value the gauge reports.</summary>
    public long AgeSecondsAt(DateTimeOffset now) => (long)AgeAt(now).TotalSeconds;
}
