using System.Diagnostics.Metrics;
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
    /// The record and the payload bytes the sink is contracted to hand the port for it — every
    /// field populated, so a change to the payload shape (a renamed field, a dropped one, a
    /// different order, a lost <c>moduleName</c>/<c>version</c>) has to show up here, in the diff.
    /// </summary>
    private static AuditEvent GoldenAuditEvent() => new()
    {
        Action = "order.cancel",
        ActorId = "user-42",
        ActorType = "user",
        Outcome = AuditOutcome.Denied,
        OnBehalfOfId = "user-7",
        TenantId = "tenant-1",
        TargetType = "order",
        TargetId = "order-9",
        Reason = "insufficient rights",
        Data = new Dictionary<string, object?> { ["attempt"] = 2 },
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
        {"moduleName":"orders-api","version":"4.0.0","auditEvent":{"action":"order.cancel","actorId":"user-42","actorType":"user","outcome":2,"onBehalfOfId":"user-7","tenantId":"tenant-1","targetType":"order","targetId":"order-9","reason":"insufficient rights","data":{"attempt":2},"oldValues":{"status":"open"},"newValues":{"status":"open"},"eventId":"0198f0c1-1111-7222-8333-444455556666","timestampUtc":"2026-08-13T10:11:12.1234567+00:00","traceId":"4bf92f3577b34da6a3ce929d0e0e4736","spanId":"00f067aa0ba902b7","sourceIp":"198.51.100.7","userAgent":"curl/8.7.1"}}
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

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public bool IsEnabled(LogLevel logLevel) => true;

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));

        public IEnumerable<string> MessagesAt(LogLevel level) =>
            Entries.Where(entry => entry.Level == level).Select(entry => entry.Message);
    }

    /// <summary>Sums the measurements of the <c>StepUpLogging.Audit</c> meter's counters, by instrument.</summary>
    private sealed class AuditMeterTotals : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Dictionary<string, long> _totals = [];

        public AuditMeterTotals()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "StepUpLogging.Audit")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            {
                lock (_totals)
                {
                    _totals[instrument.Name] = _totals.TryGetValue(instrument.Name, out var total) ? total + measurement : measurement;
                }
            });
            _listener.Start();
        }

        public long Total(string instrumentName)
        {
            lock (_totals)
            {
                return _totals.TryGetValue(instrumentName, out var total) ? total : 0;
            }
        }

        public void Dispose() => _listener.Dispose();
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
        new(Options.Create(options), encryptor, logger ?? NullLogger<EncryptedSpoolAuditSink>.Instance);

    /// <summary>A record of its own, so every write lands on a spool file of its own.</summary>
    private static AuditEvent SpoolableEvent() =>
        AuditEvent.Success("order.cancel", "user-42", "user") with
        {
            EventId = Guid.CreateVersion7(),
            TimestampUtc = DateTimeOffset.UtcNow
        };

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
        var healthCheck = new SpoolCapHealthCheck(Options.Create(options));

        Assert.Equal(AuditWriteResult.Stored, await sink.WriteAsync(SpoolableEvent()));
        Assert.Equal(HealthStatus.Healthy, (await healthCheck.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status);

        Assert.Equal(AuditWriteResult.Stored, await sink.WriteAsync(SpoolableEvent()));

        Assert.Equal(expectedStatus, (await healthCheck.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status);
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
