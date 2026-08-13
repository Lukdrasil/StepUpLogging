using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Envelopes for the tests that only care about when a record was created — ordering, same-tick
/// collisions, crash recovery. The pinned on-disk format lives in <see cref="GoldenSpoolEnvelope"/>.
/// </summary>
internal static class SpoolEnvelopes
{
    /// <summary>An envelope created at <paramref name="createdUtc"/>, with an id that sorts with it.</summary>
    internal static SpoolEnvelope CreatedAt(DateTimeOffset createdUtc) => new()
    {
        EventId = Guid.CreateVersion7(createdUtc),
        CreatedUtc = createdUtc,
        Payload = [0x2a]
    };
}
