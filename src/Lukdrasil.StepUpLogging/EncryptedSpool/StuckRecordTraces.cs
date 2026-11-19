namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

// af-stub
internal sealed class StuckRecordTraces
{
    public RecordSpan AttemptFailed(IReadOnlyList<SpoolEntry> batch, string reason) => default; // af-stub

    public RecordSpan? Settled(string spoolFilePath) => null; // af-stub

    // af-stub
    internal readonly struct RecordSpan : IDisposable
    {
        public void Dispose() // af-stub
        {
        }
    }
}
