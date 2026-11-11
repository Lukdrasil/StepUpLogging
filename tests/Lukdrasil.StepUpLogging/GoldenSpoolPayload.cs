namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// The record and the payload bytes the sink is contracted to hand the port for it, so a change
/// to the payload shape (a renamed field, a dropped one, a different order, a lost
/// <c>moduleName</c>/<c>version</c>) has to show up here, in the diff. One dictionary key is
/// mixed-case and one field is left null on purpose: caller-supplied keys travel verbatim and
/// nulls are written out, and neither survives a serializer set to rename keys or skip nulls.
/// </summary>
internal static class GoldenSpoolPayload
{
    internal const string ModuleName = "orders-api";

    internal const string Version = "4.0.0";

    internal const string Json =
        """
        {"moduleName":"orders-api","version":"4.0.0","auditEvent":{"action":"order.cancel","actorId":"user-42","actorType":"user","outcome":2,"onBehalfOfId":"user-7","tenantId":null,"targetType":"order","targetId":"order-9","reason":"insufficient rights","data":{"attempt":2,"OrderId":"9fa1"},"oldValues":{"status":"open"},"newValues":{"status":"open"},"eventId":"0198f0c1-1111-7222-8333-444455556666","timestampUtc":"2026-08-13T10:11:12.1234567+00:00","traceId":"4bf92f3577b34da6a3ce929d0e0e4736","spanId":"00f067aa0ba902b7","sourceIp":"198.51.100.7","userAgent":"curl/8.7.1"}}
        """;

    internal static AuditEvent CreateAuditEvent() => new()
    {
        Action = "order.cancel",
        ActorId = "user-42",
        ActorType = "user",
        Outcome = AuditOutcome.Denied,
        OnBehalfOfId = "user-7",
        TargetType = "order",
        TargetId = "order-9",
        Reason = "insufficient rights",
        Data = new Dictionary<string, object?> { ["attempt"] = 2, ["OrderId"] = "9fa1" },
        OldValues = new Dictionary<string, object?> { ["status"] = "open" },
        NewValues = new Dictionary<string, object?> { ["status"] = "open" },
        EventId = new Guid("0198f0c1-1111-7222-8333-444455556666"),
        TimestampUtc = new DateTimeOffset(2026, 8, 13, 10, 11, 12, TimeSpan.Zero).AddTicks(1234567),
        TraceId = "4bf92f3577b34da6a3ce929d0e0e4736",
        SpanId = "00f067aa0ba902b7",
        SourceIp = "198.51.100.7",
        UserAgent = "curl/8.7.1"
    };
}
