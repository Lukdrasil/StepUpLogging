using Microsoft.Extensions.Logging;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

// af-stub: red step for issue #69; the implementer replaces every member body.
internal sealed class DroppedRecordLog : IDisposable
{
    public DroppedRecordLog(EncryptedSpoolOptions options, TimeProvider timeProvider, ILogger logger)
    {
    }

    public void Dropped(Guid eventId, SpoolUsage usage) => throw new NotImplementedException(); // af-stub

    public void Stored() => throw new NotImplementedException(); // af-stub

    public void Dispose()
    {
        // af-stub
    }
}
