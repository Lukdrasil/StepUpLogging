using System.Diagnostics.Metrics;
using System.Net;
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// What makes a stalled drain visible: the age of the oldest record the worker is waiting on (the
/// <c>audit_spool_oldest_age_seconds</c> gauge and the opt-in health signal), and the
/// <c>reason</c> tag on <c>audit_spool_dead_lettered_total</c>.
/// </summary>
public class DrainVisibilityTests
{
    private const string DeadLetteredCounter = "audit_spool_dead_lettered_total";
    private const string ReasonTag = "reason";
    private static readonly DateTimeOffset Noon = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] DeadLetterReasons = ["rejected", "corrupt", "unreadable"];

    private static AuditEvent AuditedOperation(DateTimeOffset at) =>
        AuditEvent.Success("order.cancel", "user-42", "user") with
        {
            EventId = Guid.CreateVersion7(),
            TimestampUtc = at
        };

    private static async Task<HealthStatus> HealthOf(SpoolDrainHarness harness) =>
        (await harness.HealthCheck.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status;

    private static long OldestAgeSeconds(SpoolDrainHarness harness) =>
        harness.Head.AgeSecondsAt(harness.Time.GetUtcNow());

    private static EncryptedSpoolOptions ValidOptions() => new()
    {
        SpoolDirectory = "/var/spool/audit",
        EndpointBaseUrl = "https://audit.example/api",
        ModuleName = "orders-api",
        Version = "4.0.0"
    };

    [Fact]
    public async Task DrainWorker_HeadRetriedOn503_OldestAgeIsHeadAge_ZeroOnceDrained()
    {
        using var harness = new SpoolDrainHarness(FakeAuditReceiver.Responding(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK));
        await harness.SpoolAsync(AuditedOperation(at: Noon));
        harness.Time.SetUtcNow(Noon.AddSeconds(90));

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(90, OldestAgeSeconds(harness));

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Empty(harness.SpooledFileNames());
        Assert.Equal(0, OldestAgeSeconds(harness));
    }

    [Fact]
    public async Task DrainWorker_UnreadableHead_OldestAgeTakenFromFileName()
    {
        using var harness = new SpoolDrainHarness(FakeAuditReceiver.Responding(HttpStatusCode.OK));
        await harness.SpoolAsync(AuditedOperation(at: Noon));
        var lockedPath = Path.Combine(harness.SpoolOptions.SpoolDirectory, harness.SpooledFileNames()[0]);
        harness.Time.SetUtcNow(Noon.AddSeconds(90));

        // A locked file has no envelope to read the creation instant from, so the head's age
        // comes from the instant the file name starts with.
        using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal([Path.GetFileName(lockedPath)], harness.SpooledFileNames());
        Assert.Equal(90, OldestAgeSeconds(harness));
    }

    [Fact]
    public void SpoolHead_CreatedUtcInTheFuture_AgeIsZero()
    {
        var head = new SpoolHead();

        head.Seen(Noon.AddMinutes(5));

        Assert.Equal(TimeSpan.Zero, head.AgeAt(Noon));
        Assert.Equal(0, head.AgeSecondsAt(Noon));
    }

    [Fact]
    public void SpoolFile_CreatedUtcOrNull_RoundTripsNameFor()
    {
        var createdUtc = new DateTimeOffset(2026, 8, 13, 14, 30, 15, TimeSpan.FromHours(2)).AddTicks(1234567);
        var envelope = new SpoolEnvelope { EventId = Guid.CreateVersion7(), CreatedUtc = createdUtc, Payload = [] };

        var parsed = SpoolFile.CreatedUtcOrNull(Path.Combine("/var/spool/audit", SpoolFile.NameFor(envelope)));

        Assert.Equal(createdUtc, parsed);
        Assert.Equal(TimeSpan.Zero, parsed!.Value.Offset);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("2026.env")]
    [InlineData("2026081XT1200000000000Z-11111111-1111-7111-8111-111111111111.env")]
    public void SpoolFile_CreatedUtcOrNull_ForeignName_ReturnsNull(string fileName)
    {
        Assert.Null(SpoolFile.CreatedUtcOrNull(Path.Combine("/var/spool/audit", fileName)));
    }

    [Theory]
    [InlineData(59, null, HealthStatus.Healthy)]
    [InlineData(60, null, HealthStatus.Healthy)]
    [InlineData(61, null, HealthStatus.Degraded)]
    [InlineData(61, HealthStatus.Unhealthy, HealthStatus.Unhealthy)]
    public async Task HealthCheck_OldestRecordMaxAgeSet_ReportsConfiguredStatusOnlyPastIt(
        int ageSeconds, HealthStatus? configuredStatus, HealthStatus expectedStatus)
    {
        using var harness = new SpoolDrainHarness(
            FakeAuditReceiver.Responding(HttpStatusCode.OK),
            options =>
            {
                options.OldestRecordMaxAge = TimeSpan.FromSeconds(60);
                if (configuredStatus is not null) options.OldestRecordStatus = configuredStatus.Value;
            });
        harness.Head.Seen(Noon);
        harness.Time.SetUtcNow(Noon.AddSeconds(ageSeconds));

        Assert.Equal(expectedStatus, await HealthOf(harness));
    }

    [Fact]
    public async Task HealthCheck_OldestRecordMaxAgeUnset_StalledHeadStaysHealthy()
    {
        using var harness = new SpoolDrainHarness(FakeAuditReceiver.Responding(HttpStatusCode.OK));
        harness.Head.Seen(Noon);
        harness.Time.SetUtcNow(Noon.AddDays(1));

        Assert.Equal(HealthStatus.Healthy, await HealthOf(harness));
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("corrupt")]
    [InlineData("unreadable")]
    public async Task DrainWorker_DeadLettered_CounterTaggedByReason(string reason)
    {
        using var harness = new SpoolDrainHarness(
            FakeAuditReceiver.Responding(reason == "rejected" ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.OK),
            options => options.UnreadableRetryLimit = 1);
        using var meter = new AuditMeterTotals();

        await DeadLetterOneRecordAsync(harness, reason);

        Assert.Single(harness.DeadLetteredFileNames());
        Assert.Equal(1, meter.Total(DeadLetteredCounter, ReasonTag, reason));
        Assert.All(
            DeadLetterReasons.Where(other => other != reason),
            other => Assert.Equal(0, meter.Total(DeadLetteredCounter, ReasonTag, other)));
    }

    private static async Task DeadLetterOneRecordAsync(SpoolDrainHarness harness, string reason)
    {
        switch (reason)
        {
            case "rejected":
                await harness.SpoolAsync(AuditedOperation(at: Noon));
                await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);
                break;

            case "corrupt":
                harness.PutInSpool("20200101T0000000000000Z-11111111-1111-7111-8111-111111111111.env", "not an envelope");
                await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);
                break;

            default:
                await harness.SpoolAsync(AuditedOperation(at: Noon));
                var lockedPath = Path.Combine(harness.SpoolOptions.SpoolDirectory, harness.SpooledFileNames()[0]);
                // FileShare.Delete on Windows so the dead-letter move still succeeds; on Unix
                // None is what makes the read fail (see DrainWorkerTests).
                var unreadable = OperatingSystem.IsWindows() ? FileShare.Delete : FileShare.None;
                using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, unreadable))
                {
                    await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);
                }

                break;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_OldestRecordMaxAgeZeroOrNegative_FailsNamingTheOption(int milliseconds)
    {
        var options = ValidOptions();
        options.OldestRecordMaxAge = TimeSpan.FromMilliseconds(milliseconds);

        var result = new EncryptedSpoolOptionsValidator().Validate(name: null, options);

        Assert.True(result.Failed, "OldestRecordMaxAge was accepted");
        Assert.Contains(
            result.Failures!,
            failure => failure.Contains($"{nameof(EncryptedSpoolOptions)}.{nameof(EncryptedSpoolOptions.OldestRecordMaxAge)}"));
    }

    [Fact]
    public void Validate_OldestRecordMaxAgeNull_Passes()
    {
        var options = ValidOptions();
        options.OldestRecordMaxAge = null;

        Assert.True(new EncryptedSpoolOptionsValidator().Validate(name: null, options).Succeeded);
    }

    [Fact]
    public async Task AddEncryptedSpoolAuditSink_PublishesOldestAgeGauge_InSeconds()
    {
        using var spool = new TempSpoolDirectory();
        var builder = TestHosts.NewBuilder();
        builder.AddStepUpLogging();
        builder.Services.AddSingleton<IAuditPayloadEncryptor, FakeAuditPayloadEncryptor>();
        builder.AddEncryptedSpoolAuditSink(options =>
        {
            options.SpoolDirectory = spool.FullPath;
            options.EndpointBaseUrl = "https://audit.example/api";
            options.ModuleName = "orders-api";
            options.Version = "4.0.0";
        });
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            List<Instrument> published = [];
            using var listener = new MeterListener
            {
                InstrumentPublished = (instrument, _) =>
                {
                    if (instrument.Meter.Name == "StepUpLogging.Audit") published.Add(instrument);
                }
            };
            listener.Start();

            Assert.Contains(
                published,
                instrument => instrument is ObservableGauge<long> { Name: "audit_spool_oldest_age_seconds", Unit: "s" });
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
