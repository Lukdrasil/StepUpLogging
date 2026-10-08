namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>The creation instant of the oldest record the drain worker is waiting on.</summary>
internal sealed class SpoolHead // af-stub
{
    public void Seen(DateTimeOffset? createdUtc) => throw new NotImplementedException(); // af-stub

    public void Cleared() => throw new NotImplementedException(); // af-stub

    public TimeSpan AgeAt(DateTimeOffset now) => throw new NotImplementedException(); // af-stub

    public long AgeSecondsAt(DateTimeOffset now) => throw new NotImplementedException(); // af-stub
}
