using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// One envelope with fixed field values, together with the file name and the bytes the spool is
/// contracted to produce for it. The literals are the pinned on-disk format: a change to the
/// envelope shape or the naming rule has to show up here, in the diff.
/// </summary>
internal static class GoldenSpoolEnvelope
{
    internal const string FileName = "20260813T1011121234567Z-0198f0c1-1111-7222-8333-444455556666.env";

    internal const string Json =
        """{"eventId":"0198f0c1-1111-7222-8333-444455556666","createdUtc":"2026-08-13T10:11:12.1234567+00:00","payload":"AQID"}""";

    internal static SpoolEnvelope Create() => new()
    {
        EventId = new Guid("0198f0c1-1111-7222-8333-444455556666"),
        CreatedUtc = new DateTimeOffset(2026, 8, 13, 10, 11, 12, TimeSpan.Zero).AddTicks(1234567),
        Payload = [0x01, 0x02, 0x03]
    };
}
