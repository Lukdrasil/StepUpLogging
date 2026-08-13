using System.Text;
using System.Text.Json;
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Behaviour of <see cref="EncryptedSpoolAuditSink"/> over a real spool directory: what it hands
/// the encryption port, what it leaves on disk, and the one condition under which it reports
/// <see cref="AuditWriteResult.Dropped"/> instead of storing the record.
/// </summary>
public class EncryptedSpoolAuditSinkTests
{
    private static readonly JsonSerializerOptions PayloadJsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// The record and the payload bytes the sink is contracted to hand the port for it, so a change
    /// to the payload shape (a renamed field, a dropped one, a different order, a lost
    /// <c>moduleName</c>/<c>version</c>) has to show up here, in the diff. One dictionary key is
    /// mixed-case and one field is left null on purpose: caller-supplied keys travel verbatim and
    /// nulls are written out, and neither survives a serializer set to rename keys or skip nulls.
    /// </summary>
    private static AuditEvent GoldenAuditEvent() => new()
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

    private const string GoldenPayloadJson =
        """
        {"moduleName":"orders-api","version":"4.0.0","auditEvent":{"action":"order.cancel","actorId":"user-42","actorType":"user","outcome":2,"onBehalfOfId":"user-7","tenantId":null,"targetType":"order","targetId":"order-9","reason":"insufficient rights","data":{"attempt":2,"OrderId":"9fa1"},"oldValues":{"status":"open"},"newValues":{"status":"open"},"eventId":"0198f0c1-1111-7222-8333-444455556666","timestampUtc":"2026-08-13T10:11:12.1234567+00:00","traceId":"4bf92f3577b34da6a3ce929d0e0e4736","spanId":"00f067aa0ba902b7","sourceIp":"198.51.100.7","userAgent":"curl/8.7.1"}}
        """;

    /// <summary>Records what the sink handed the port, so the payload can be asserted on directly.</summary>
    private sealed class CapturingAuditPayloadEncryptor : IAuditPayloadEncryptor
    {
        public List<byte[]> Payloads { get; } = [];

        public ValueTask<byte[]> EncryptAsync(ReadOnlyMemory<byte> payload)
        {
            Payloads.Add(payload.ToArray());
            return ValueTask.FromResult<byte[]>([0xde, 0xad]);
        }
    }

    /// <summary>An encryptor that yields before failing, so a sink that forgets to await sees the failure too.</summary>
    private sealed class ThrowingAuditPayloadEncryptor(Exception failure) : IAuditPayloadEncryptor
    {
        public async ValueTask<byte[]> EncryptAsync(ReadOnlyMemory<byte> payload)
        {
            await Task.Yield();
            throw failure;
        }
    }

    /// <summary>A value System.Text.Json cannot write: reading it walks in a circle.</summary>
    private sealed class SelfReferencingValue
    {
        public SelfReferencingValue Itself => this;
    }

    private static EncryptedSpoolOptions OptionsFor(TempSpoolDirectory spool, Action<EncryptedSpoolOptions>? configure = null)
    {
        var options = new EncryptedSpoolOptions
        {
            SpoolDirectory = spool.FullPath,
            ModuleName = "orders-api",
            Version = "4.0.0"
        };
        configure?.Invoke(options);
        return options;
    }

    private static EncryptedSpoolAuditSink CreateSink(
        EncryptedSpoolOptions options,
        IAuditPayloadEncryptor encryptor,
        ILogger<EncryptedSpoolAuditSink>? logger = null) =>
        new(Options.Create(options), new SpoolWriter(options.SpoolDirectory), encryptor, logger ?? NullLogger<EncryptedSpoolAuditSink>.Instance);

    private static EncryptedSpoolHealthCheck CreateHealthCheck(EncryptedSpoolOptions options) =>
        new(Options.Create(options), new DeadLetterBox(Options.Create(options)), new EndpointReachability());

    /// <summary>A record of its own, so every write lands on a spool file of its own.</summary>
    private static AuditEvent SpoolableEvent() =>
        AuditEvent.Success("order.cancel", "user-42", "user") with
        {
            EventId = Guid.CreateVersion7(),
            TimestampUtc = DateTimeOffset.UtcNow
        };

    /// <summary>The envelope the sink writes for <paramref name="auditEvent"/>, for tests that need its file name.</summary>
    private static SpoolEnvelope EnvelopeFor(AuditEvent auditEvent) =>
        new() { EventId = auditEvent.EventId, CreatedUtc = auditEvent.TimestampUtc, Payload = [] };

    private static int SpooledRecordCount(TempSpoolDirectory spool) =>
        Directory.GetFiles(spool.FullPath, "*.env").Length;

    [Fact]
    public async Task WriteAsync_NormalRecord_HandsThePortThePayloadPinnedWithModuleNameAndVersion()
    {
        using var spool = new TempSpoolDirectory();
        var encryptor = new CapturingAuditPayloadEncryptor();
        using var sink = CreateSink(OptionsFor(spool), encryptor);

        await sink.WriteAsync(GoldenAuditEvent());

        Assert.Equal(GoldenPayloadJson, Encoding.UTF8.GetString(Assert.Single(encryptor.Payloads)));
    }

    [Fact]
    public async Task WriteAsync_NormalRecord_SpoolsAnEnvelopeWhosePayloadDecryptsBackToTheRecord()
    {
        using var spool = new TempSpoolDirectory();
        var encryptor = new FakeAuditPayloadEncryptor();
        using var sink = CreateSink(OptionsFor(spool), encryptor);
        var auditEvent = GoldenAuditEvent();

        var result = await sink.WriteAsync(auditEvent);

        Assert.Equal(AuditWriteResult.Stored, result);
        var entry = Assert.Single(new SpoolReader(spool.FullPath).ReadOldestFirst());
        Assert.Equal(auditEvent.EventId, entry.Envelope!.EventId);
        Assert.Equal(auditEvent.TimestampUtc, entry.Envelope.CreatedUtc);

        // What a receiver gets back for every field is B08's round-trip test, once a drained record
        // is what it reconstructs from; here the golden payload above pins the serialized shape.
        var payload = JsonSerializer.Deserialize<SpoolPayload>(encryptor.Decrypt(entry.Envelope.Payload), PayloadJsonOptions)!;
        Assert.Equal("orders-api", payload.ModuleName);
        Assert.Equal("4.0.0", payload.Version);
        Assert.Equal(auditEvent.EventId, payload.AuditEvent.EventId);
        Assert.Equal(auditEvent.Action, payload.AuditEvent.Action);
        Assert.Equal(auditEvent.ActorId, payload.AuditEvent.ActorId);
        Assert.Equal(auditEvent.ActorType, payload.AuditEvent.ActorType);
        Assert.Equal(auditEvent.Outcome, payload.AuditEvent.Outcome);
        Assert.Equal(auditEvent.TimestampUtc, payload.AuditEvent.TimestampUtc);
        Assert.Equal(auditEvent.TraceId, payload.AuditEvent.TraceId);
    }

    [Fact]
    public async Task WriteAsync_SpoolAtItsCap_ReturnsDroppedAndLogsCriticalWithoutWritingTheRecord()
    {
        using var spool = new TempSpoolDirectory();
        using var meter = new AuditMeterTotals();
        var logger = new RecordingLogger<EncryptedSpoolAuditSink>();
        using var sink = CreateSink(OptionsFor(spool, options => options.SpoolMaxEntries = 3), new FakeAuditPayloadEncryptor(), logger);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(AuditWriteResult.Stored, await sink.WriteAsync(SpoolableEvent()));
        }

        var rejectedBefore = meter.Total("audit_spool_rejected_full_total");
        var overflowing = SpoolableEvent();
        var result = await sink.WriteAsync(overflowing);

        Assert.Equal(AuditWriteResult.Dropped, result);
        Assert.Equal(3, SpooledRecordCount(spool));
        Assert.DoesNotContain(overflowing.EventId.ToString(), string.Join(' ', Directory.GetFiles(spool.FullPath)));
        Assert.Contains(logger.MessagesAt(LogLevel.Critical), message => message.Contains(overflowing.EventId.ToString()));
        Assert.Equal(rejectedBefore + 1, meter.Total("audit_spool_rejected_full_total"));
    }

    [Fact]
    public async Task AuditAsync_WhenTheSpoolIsAtItsCap_CountsTheDropInsteadOfTheWriteAndSkipsTheCompanionLog()
    {
        using var spool = new TempSpoolDirectory();
        using var sink = CreateSink(OptionsFor(spool, options => options.SpoolMaxEntries = 1), new FakeAuditPayloadEncryptor());
        var auditLogger = new AuditLogger<EncryptedSpoolAuditSinkTests>(
            sink,
            NullLogger<EncryptedSpoolAuditSinkTests>.Instance,
            new HttpContextAccessor(),
            Options.Create(new StepUpLoggingOptions()),
            new CompiledRedactionPatterns([]));
        await auditLogger.AuditAsync(AuditEvent.Success("order.cancel", "user-42", "user"));

        using var meter = new AuditMeterTotals();
        var companionLogged = false;

        await auditLogger.AuditAsync(AuditEvent.Success("order.cancel", "user-42", "user"), _ => companionLogged = true);

        Assert.Equal(0, meter.Total("audit_events_total"));
        Assert.Equal(1, meter.Total("audit_events_dropped_total"));
        Assert.False(companionLogged);
    }

    [Theory]
    [InlineData(null, HealthStatus.Unhealthy)]
    [InlineData(HealthStatus.Degraded, HealthStatus.Degraded)]
    public async Task CheckHealthAsync_SpoolAtHalfItsCap_ReportsTheConfiguredWarnStatusWhileWritesStillSucceed(
        HealthStatus? configuredStatus, HealthStatus expectedStatus)
    {
        using var spool = new TempSpoolDirectory();
        var options = OptionsFor(spool, options =>
        {
            options.SpoolMaxEntries = 4;
            if (configuredStatus is not null) options.SpoolWarnStatus = configuredStatus.Value;
        });
        using var sink = CreateSink(options, new FakeAuditPayloadEncryptor());
        var healthCheck = CreateHealthCheck(options);

        Assert.Equal(AuditWriteResult.Stored, await sink.WriteAsync(SpoolableEvent()));
        Assert.Equal(HealthStatus.Healthy, (await healthCheck.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status);

        Assert.Equal(AuditWriteResult.Stored, await sink.WriteAsync(SpoolableEvent()));

        Assert.Equal(expectedStatus, (await healthCheck.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task CheckHealthAsync_SpoolAtItsCap_ReportsTheFullStatusRatherThanTheMilderWarnStatus()
    {
        using var spool = new TempSpoolDirectory();
        var options = OptionsFor(spool, options =>
        {
            options.SpoolMaxEntries = 2;
            options.SpoolWarnStatus = HealthStatus.Degraded;
        });
        using var sink = CreateSink(options, new FakeAuditPayloadEncryptor());
        var healthCheck = CreateHealthCheck(options);

        await sink.WriteAsync(SpoolableEvent());
        Assert.Equal(HealthStatus.Degraded, (await healthCheck.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status);

        await sink.WriteAsync(SpoolableEvent());

        // An instance that is already losing audit records must not read as the milder status an
        // operator chose for "the spool is filling up".
        Assert.Equal(HealthStatus.Unhealthy, (await healthCheck.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task CheckHealthAsync_CancelledProbe_StopsInsteadOfScanningTheSpool()
    {
        using var spool = new TempSpoolDirectory();
        var healthCheck = CreateHealthCheck(OptionsFor(spool));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await healthCheck.CheckHealthAsync(new HealthCheckContext(), cancelled.Token));
    }

    [Fact]
    public async Task WriteAsync_TemporaryFileLeftBehindInTheSpool_CountsAgainstTheCap()
    {
        using var spool = new TempSpoolDirectory();
        using var sink = CreateSink(OptionsFor(spool, options => options.SpoolMaxEntries = 1), new FakeAuditPayloadEncryptor());

        // Bytes a failed or interrupted write left behind occupy the spool exactly as a record
        // does, and nothing clears them before the next start-up.
        File.WriteAllText(Path.Combine(spool.FullPath, "20260813T1200000000000Z-orphan.tmp"), "{}");

        Assert.Equal(AuditWriteResult.Dropped, await sink.WriteAsync(SpoolableEvent()));
    }

    [Fact]
    public async Task WriteAsync_AfterAWriteFailed_CountsWhatTheFailureLeftInTheSpool()
    {
        using var spool = new TempSpoolDirectory();
        using var sink = CreateSink(OptionsFor(spool, options => options.SpoolMaxEntries = 2), new FakeAuditPayloadEncryptor());
        await sink.WriteAsync(SpoolableEvent());
        var doomed = SpoolableEvent();
        var temporaryPath = Path.ChangeExtension(
            Path.Combine(spool.FullPath, SpoolFile.NameFor(EnvelopeFor(doomed))),
            SpoolFile.TemporaryExtension);

        using (new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(async () => await sink.WriteAsync(doomed));
        }

        // The failure left a second file in the spool, which fills it: a sink that kept adding up
        // its own successful writes would still believe there is room for one more.
        Assert.Equal(AuditWriteResult.Dropped, await sink.WriteAsync(SpoolableEvent()));
    }

    [Fact]
    public async Task WriteAsync_AfterTheDrainWorkerEmptiedAFullSpool_StoresAgainInsteadOfTrustingTheOlderVerdict()
    {
        using var spool = new TempSpoolDirectory();
        using var sink = CreateSink(OptionsFor(spool, options => options.SpoolMaxEntries = 3), new FakeAuditPayloadEncryptor());
        for (var i = 0; i < 3; i++)
        {
            await sink.WriteAsync(SpoolableEvent());
        }

        Assert.Equal(AuditWriteResult.Dropped, await sink.WriteAsync(SpoolableEvent()));
        foreach (var delivered in Directory.GetFiles(spool.FullPath))
        {
            File.Delete(delivered);
        }

        Assert.Equal(AuditWriteResult.Stored, await sink.WriteAsync(SpoolableEvent()));
    }

    [Fact]
    public void SpoolDirectory_HasNoDefault_SoItCannotSilentlyLandInsideTheDeployment()
    {
        // A path under the deployment folder is the container's ephemeral layer: a restart or a
        // redeploy would destroy records WriteAsync already reported durable.
        Assert.Empty(new EncryptedSpoolOptions().SpoolDirectory);
    }

    [Fact]
    public async Task WriteAsync_SpoolReachesHalfItsCap_LogsErrorOnceRatherThanOnEveryWriteAbove()
    {
        using var spool = new TempSpoolDirectory();
        var logger = new RecordingLogger<EncryptedSpoolAuditSink>();
        using var sink = CreateSink(OptionsFor(spool, options => options.SpoolMaxEntries = 4), new FakeAuditPayloadEncryptor(), logger);

        for (var i = 0; i < 4; i++)
        {
            await sink.WriteAsync(SpoolableEvent());
        }

        Assert.Single(logger.MessagesAt(LogLevel.Error));
    }

    [Fact]
    public async Task WriteAsync_RecordOverThePayloadCap_ThrowsNamingTheCapBeforeEncryptingOrSpooling()
    {
        using var spool = new TempSpoolDirectory();
        var encryptor = new CapturingAuditPayloadEncryptor();
        using var sink = CreateSink(OptionsFor(spool, options => options.MaxPayloadBytes = 512), encryptor);
        var oversized = SpoolableEvent() with
        {
            Data = new Dictionary<string, object?> { ["blob"] = new string('x', 1024) }
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () => await sink.WriteAsync(oversized));

        Assert.Contains(nameof(EncryptedSpoolOptions.MaxPayloadBytes), exception.Message);
        Assert.Contains("512", exception.Message);
        Assert.Empty(encryptor.Payloads);
        Assert.Equal(0, SpooledRecordCount(spool));
    }

    [Fact]
    public async Task WriteAsync_RecordThatCannotBeSerialized_ThrowsBeforeEncryptingOrSpooling()
    {
        using var spool = new TempSpoolDirectory();
        var encryptor = new CapturingAuditPayloadEncryptor();
        using var sink = CreateSink(OptionsFor(spool), encryptor);
        var unserializable = SpoolableEvent() with
        {
            Data = new Dictionary<string, object?> { ["loop"] = new SelfReferencingValue() }
        };

        await Assert.ThrowsAsync<JsonException>(async () => await sink.WriteAsync(unserializable));

        Assert.Empty(encryptor.Payloads);
        Assert.Equal(0, SpooledRecordCount(spool));
    }

    [Fact]
    public async Task WriteAsync_PortThrows_PropagatesThatExceptionUnchangedAndSpoolsNothing()
    {
        using var spool = new TempSpoolDirectory();
        var failure = new InvalidOperationException("the consumer's encryptor could not reach its key store");
        using var sink = CreateSink(OptionsFor(spool), new ThrowingAuditPayloadEncryptor(failure));

        // A port failure is never a spool-cap condition: turning it into Dropped would erase the
        // record and the operator's only signal that encryption is broken (ADR 0016 D2).
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await sink.WriteAsync(SpoolableEvent()));

        Assert.Same(failure, thrown);
        Assert.Equal(0, SpooledRecordCount(spool));
    }

    [Fact]
    public async Task WriteAsync_ConcurrentWritesAtTheCapBoundary_StoreExactlyTheCapacityLeft()
    {
        using var spool = new TempSpoolDirectory();
        using var sink = CreateSink(OptionsFor(spool, options => options.SpoolMaxEntries = 8), new FakeAuditPayloadEncryptor());
        for (var i = 0; i < 6; i++)
        {
            await sink.WriteAsync(SpoolableEvent());
        }

        var results = await Task.WhenAll(Enumerable
            .Range(0, 10)
            .Select(_ => Task.Run(async () => await sink.WriteAsync(SpoolableEvent()))));

        Assert.Equal(2, results.Count(result => result == AuditWriteResult.Stored));
        Assert.Equal(8, results.Count(result => result == AuditWriteResult.Dropped));
        Assert.Equal(8, SpooledRecordCount(spool));
    }
}
