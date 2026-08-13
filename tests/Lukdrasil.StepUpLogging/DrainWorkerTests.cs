using System.Net;
using System.Text.Json;
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Behaviour of <see cref="DrainWorker"/> over a real spool directory and a faked audit receiver:
/// what it sends, what it deletes, what it sets aside in <c>dead-letter/</c>, and what it keeps for
/// another attempt (ADR 0020 D7).
/// </summary>
public class DrainWorkerTests
{
    private static readonly JsonSerializerOptions PayloadJsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The audit receiver, faked at the HTTP boundary: it records what was posted and answers as the test lined up.</summary>
    private sealed class FakeAuditReceiver(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private readonly List<(Uri Uri, SpoolEnvelope Envelope)> _requests = [];

        /// <summary>Answers every request with <paramref name="statuses"/> in turn, the last one repeating.</summary>
        public static FakeAuditReceiver Responding(params HttpStatusCode[] statuses) =>
            new((requestIndex, _) => Task.FromResult(new HttpResponseMessage(statuses[Math.Min(requestIndex, statuses.Length - 1)])));

        /// <summary>Fails every request with <paramref name="failure"/>, after recording what it was sent.</summary>
        public static FakeAuditReceiver Failing(Exception failure) =>
            new((_, _) => Task.FromException<HttpResponseMessage>(failure));

        /// <summary>Never answers, so a request only ends when the caller gives up on it.</summary>
        public static FakeAuditReceiver Hanging() =>
            new(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        /// <summary>
        /// Answers as though a redirect had already been followed to <paramref name="finalUri"/>:
        /// the response names a request URI other than the one the worker actually posted to,
        /// which is what an auto-following handler would leave behind.
        /// </summary>
        public static FakeAuditReceiver RespondingFromADifferentUri(Uri finalUri, HttpStatusCode status) =>
            new((_, _) => Task.FromResult(new HttpResponseMessage(status) { RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUri) }));

        public IReadOnlyList<SpoolEnvelope> Received
        {
            get { lock (_requests) { return [.. _requests.Select(request => request.Envelope)]; } }
        }

        public IReadOnlyList<Uri> RequestedUris
        {
            get { lock (_requests) { return [.. _requests.Select(request => request.Uri)]; } }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            int requestIndex;
            lock (_requests)
            {
                _requests.Add((request.RequestUri!, JsonSerializer.Deserialize<SpoolEnvelope>(body)!));
                requestIndex = _requests.Count - 1;
            }

            return await respond(requestIndex, cancellationToken);
        }
    }

    /// <summary>Hands out the one client the test's receiver is behind, as <c>AddHttpClient</c> does in production.</summary>
    private sealed class SingleClientHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    /// <summary>
    /// A drain worker over its own temporary spool, with the sink that fills it, the receiver it
    /// posts to, and the health check that reports on it — all wired the way
    /// <c>AddEncryptedSpoolAuditSink</c> wires them.
    /// </summary>
    private sealed class DrainHarness : IDisposable
    {
        private readonly TempSpoolDirectory _root = new();
        private readonly HttpClient _client;
        private readonly EncryptedSpoolAuditSink _sink;

        public DrainHarness(FakeAuditReceiver receiver, Action<EncryptedSpoolOptions>? configure = null)
        {
            SpoolOptions = new EncryptedSpoolOptions
            {
                // A spool below the temporary root, so its dead-letter sibling is thrown away with it.
                SpoolDirectory = Path.Combine(_root.FullPath, "spool"),
                ModuleName = "orders-api",
                Version = "4.0.0",
                EndpointBaseUrl = "https://audit.example/api"
            };
            configure?.Invoke(SpoolOptions);

            Receiver = receiver;
            _client = new HttpClient(receiver);
            _sink = new EncryptedSpoolAuditSink(Options.Create(SpoolOptions), new SpoolWriter(SpoolOptions.SpoolDirectory), Encryptor, NullLogger<EncryptedSpoolAuditSink>.Instance);
            DeadLetter = new DeadLetterBox(Options.Create(SpoolOptions));
            Reachability = new EndpointReachability();
            Worker = StartedOver();
            HealthCheck = new EncryptedSpoolHealthCheck(Options.Create(SpoolOptions), DeadLetter, Reachability);
        }

        public EncryptedSpoolOptions SpoolOptions { get; }

        public FakeAuditReceiver Receiver { get; }

        public FakeAuditPayloadEncryptor Encryptor { get; } = new();

        public FakeTimeProvider Time { get; } = new();

        public RecordingLogger<DrainWorker> Logger { get; } = new();

        public DeadLetterBox DeadLetter { get; }

        public EndpointReachability Reachability { get; }

        public DrainWorker Worker { get; }

        public EncryptedSpoolHealthCheck HealthCheck { get; }

        /// <summary>Another worker over the same spool, endpoint and directories — what a restarted host builds.</summary>
        public DrainWorker StartedOver() =>
            new(Options.Create(SpoolOptions), new SingleClientHttpClientFactory(_client), DeadLetter, Reachability, Time, Logger);

        /// <summary>Spools <paramref name="auditEvent"/> through the sink, exactly as an audited operation does.</summary>
        public async Task<AuditEvent> SpoolAsync(AuditEvent auditEvent)
        {
            Assert.Equal(AuditWriteResult.Stored, await _sink.WriteAsync(auditEvent));
            return auditEvent;
        }

        /// <summary>The audit record inside a delivered envelope, decrypted the way the receiver does.</summary>
        public AuditEvent RecordIn(SpoolEnvelope envelope) =>
            JsonSerializer.Deserialize<SpoolPayload>(Encryptor.Decrypt(envelope.Payload), PayloadJsonOptions)!.AuditEvent;

        public IReadOnlyList<string> SpooledFileNames() => FileNamesIn(SpoolOptions.SpoolDirectory);

        public IReadOnlyList<string> DeadLetteredFileNames() => FileNamesIn(DeadLetter.DirectoryPath);

        /// <summary>Puts <paramref name="contents"/> into the spool under <paramref name="fileName"/>, as a fault on the disk would leave it.</summary>
        public string PutInSpool(string fileName, string contents)
        {
            var path = Path.Combine(SpoolOptions.SpoolDirectory, fileName);
            File.WriteAllText(path, contents);
            return path;
        }

        private static IReadOnlyList<string> FileNamesIn(string directory) =>
            Directory.Exists(directory)
                ? [.. Directory.EnumerateFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal)!]
                : [];

        public void Dispose()
        {
            _sink.Dispose();
            _client.Dispose();
            Receiver.Dispose();
            _root.Dispose();
        }
    }

    private static readonly DateTimeOffset Noon = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    private static AuditEvent AuditedOperation(string action = "order.cancel", DateTimeOffset? at = null) =>
        AuditEvent.Success(action, "user-42", "user") with
        {
            EventId = Guid.CreateVersion7(),
            TimestampUtc = at ?? DateTimeOffset.UtcNow
        };

    private static async Task<HealthStatus> HealthOf(DrainHarness harness) =>
        (await harness.HealthCheck.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status;

    [Fact]
    public async Task DrainWorker_RoundTripsThroughFakeEncryptor_ReconstructsOriginalAuditEvent()
    {
        using var harness = new DrainHarness(FakeAuditReceiver.Responding(HttpStatusCode.OK));
        var original = AuditedOperation() with
        {
            Outcome = AuditOutcome.Denied,
            OnBehalfOfId = "user-7",
            TenantId = "tenant-3",
            TargetType = "order",
            TargetId = "order-9",
            Reason = "insufficient rights",
            Data = new Dictionary<string, object?> { ["attempt"] = 2 },
            OldValues = new Dictionary<string, object?> { ["status"] = "open" },
            NewValues = new Dictionary<string, object?> { ["status"] = "cancelled" },
            TraceId = "4bf92f3577b34da6a3ce929d0e0e4736",
            SpanId = "00f067aa0ba902b7",
            SourceIp = "198.51.100.7",
            UserAgent = "curl/8.7.1"
        };
        await harness.SpoolAsync(original);

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("https://audit.example/api/audit"), Assert.Single(harness.Receiver.RequestedUris));
        var delivered = Assert.Single(harness.Receiver.Received);
        Assert.Equal(original.EventId, delivered.EventId);
        Assert.Equal(original.TimestampUtc, delivered.CreatedUtc);

        var reconstructed = harness.RecordIn(delivered);
        var noDictionaries = (AuditEvent record) => record with { Data = null, OldValues = null, NewValues = null };
        Assert.Equal(noDictionaries(original), noDictionaries(reconstructed));
        Assert.Equal("2", reconstructed.Data!["attempt"]?.ToString());
        Assert.Equal("open", reconstructed.OldValues!["status"]?.ToString());
        Assert.Equal("cancelled", reconstructed.NewValues!["status"]?.ToString());

        // Deleted only once the receiver confirmed it stored the record (ADR 0020 D7).
        Assert.Empty(harness.SpooledFileNames());
    }

    [Fact]
    public async Task DrainWorker_DrainsOldestFirst()
    {
        using var harness = new DrainHarness(FakeAuditReceiver.Responding(HttpStatusCode.OK));
        var newest = await harness.SpoolAsync(AuditedOperation("order.refund", at: Noon.AddMinutes(10)));
        var oldest = await harness.SpoolAsync(AuditedOperation("order.create", at: Noon));
        var middle = await harness.SpoolAsync(AuditedOperation("order.cancel", at: Noon.AddMinutes(5)));

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [oldest.EventId, middle.EventId, newest.EventId],
            harness.Receiver.Received.Select(envelope => envelope.EventId));
        Assert.Empty(harness.SpooledFileNames());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(HttpStatusCode.UnsupportedMediaType)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task DrainWorker_PermanentStatusCode_DeadLettersImmediately_LogsCritical_HealthUnhealthy_QueueContinues(
        HttpStatusCode permanentRejection)
    {
        using var harness = new DrainHarness(FakeAuditReceiver.Responding(permanentRejection, HttpStatusCode.OK));
        var rejected = await harness.SpoolAsync(AuditedOperation(at: Noon));
        var accepted = await harness.SpoolAsync(AuditedOperation(at: Noon.AddMinutes(1)));
        var rejectedFileName = harness.SpooledFileNames()[0];
        using var meter = new AuditMeterTotals();

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        // Retrying cannot fix a request the receiver rejected on its merits, so the record is set
        // aside at once and the queue moves on to the next one (ADR 0020 D7).
        Assert.Equal([rejected.EventId, accepted.EventId], harness.Receiver.Received.Select(envelope => envelope.EventId));
        Assert.Empty(harness.SpooledFileNames());
        Assert.Equal([rejectedFileName], harness.DeadLetteredFileNames());
        Assert.Contains(harness.Logger.MessagesAt(LogLevel.Critical), message => message.Contains(rejected.EventId.ToString()));
        Assert.Equal(1, meter.Total("audit_spool_dead_lettered_total"));
        Assert.Equal(1, meter.Total("audit_spool_drained_total"));
        Assert.Equal(HealthStatus.Unhealthy, await HealthOf(harness));
    }

    [Fact]
    public async Task DrainWorker_CorruptSpoolFile_DeadLettersItWithoutSendingItAndKeepsDraining()
    {
        using var harness = new DrainHarness(FakeAuditReceiver.Responding(HttpStatusCode.OK));
        var readable = await harness.SpoolAsync(AuditedOperation(at: Noon));
        var corrupt = harness.PutInSpool("20200101T0000000000000Z-11111111-1111-7111-8111-111111111111.env", "not an envelope");

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal([readable.EventId], harness.Receiver.Received.Select(envelope => envelope.EventId));
        Assert.Empty(harness.SpooledFileNames());
        Assert.Equal([Path.GetFileName(corrupt)], harness.DeadLetteredFileNames());
        Assert.Contains(harness.Logger.MessagesAt(LogLevel.Critical), message => message.Contains(Path.GetFileName(corrupt)));
        Assert.Equal(HealthStatus.Unhealthy, await HealthOf(harness));
    }

    [Fact]
    public async Task DrainWorker_SpoolFileIsLockedWhenRead_LeavesItSpooledForRetryInsteadOfDeadLettering()
    {
        using var harness = new DrainHarness(FakeAuditReceiver.Responding(HttpStatusCode.OK));
        var locked = await harness.SpoolAsync(AuditedOperation(at: Noon));
        var lockedFileName = harness.SpooledFileNames()[0];
        var lockedPath = Path.Combine(harness.SpoolOptions.SpoolDirectory, lockedFileName);
        using var meter = new AuditMeterTotals();

        // A sharing violation is not a defect in the record — unlike malformed JSON, the same file
        // read a moment later succeeds, so it must stay spooled rather than be dead-lettered.
        using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);
        }

        Assert.Empty(harness.Receiver.Received);
        Assert.Equal([lockedFileName], harness.SpooledFileNames());
        Assert.Empty(harness.DeadLetteredFileNames());
        Assert.Equal(1, meter.Total("audit_spool_drain_failures_total"));

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal([locked.EventId], harness.Receiver.Received.Select(envelope => envelope.EventId));
        Assert.Empty(harness.SpooledFileNames());
    }

    [Fact]
    public async Task DrainWorker_SpoolFileStaysUnreadablePastTheRetryLimit_DeadLettersItWithoutTouchingEndpointReachability()
    {
        using var harness = new DrainHarness(
            FakeAuditReceiver.Responding(HttpStatusCode.OK),
            options => options.UnreadableRetryLimit = 2);
        var locked = await harness.SpoolAsync(AuditedOperation(at: Noon));
        var lockedFileName = harness.SpooledFileNames()[0];
        var lockedPath = Path.Combine(harness.SpoolOptions.SpoolDirectory, lockedFileName);
        using var meter = new AuditMeterTotals();

        // A fault that never clears (a bad sector, a broken ACL) must not block the queue forever
        // (ADR 0020's own rejected alternative), and the endpoint was never contacted for it, so it
        // must not count against EndpointReachability — that would blame the receiver for a local
        // disk fault. FileShare.Delete (not None): the dead-letter move needs directory rights, not
        // read access to the file, exactly as ADR 0020 D7's rationale for a permission fault says —
        // a lock that also blocked the move would never let this test's own dead-letter succeed.
        using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Delete))
        {
            await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);
            await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);
        }

        Assert.Empty(harness.Receiver.Received);
        Assert.Empty(harness.SpooledFileNames());
        Assert.Equal([lockedFileName], harness.DeadLetteredFileNames());
        Assert.Contains(harness.Logger.MessagesAt(LogLevel.Critical), message => message.Contains(lockedFileName));
        Assert.Equal(0, harness.Reachability.ConsecutiveFailures);
        Assert.Equal(1, meter.Total("audit_spool_dead_lettered_total"));
    }

    /// <summary>The receiver behaviours ADR 0020 D7 calls transient: retry, never dead-letter.</summary>
    private static FakeAuditReceiver TransientlyFailing(string failure) => failure switch
    {
        "500" => FakeAuditReceiver.Responding(HttpStatusCode.InternalServerError),
        "503" => FakeAuditReceiver.Responding(HttpStatusCode.ServiceUnavailable),
        "408" => FakeAuditReceiver.Responding(HttpStatusCode.RequestTimeout),
        "429" => FakeAuditReceiver.Responding(HttpStatusCode.TooManyRequests),
        // Not a defect in the record: an expired or misconfigured credential (401/403), or a
        // receiver mid-rollout that does not yet recognise the route (404). Dead-lettering these
        // would walk the whole spool at machine speed for the duration of an outage that fixes
        // itself (ADR 0020 D7).
        "401" => FakeAuditReceiver.Responding(HttpStatusCode.Unauthorized),
        "404" => FakeAuditReceiver.Responding(HttpStatusCode.NotFound),
        // A redirect the worker must see as itself, not follow: AllowAutoRedirect on the named
        // client would rewrite this POST into a body-less GET (B09 hand-off).
        "302" => FakeAuditReceiver.Responding(HttpStatusCode.Redirect),
        "network-failure" => FakeAuditReceiver.Failing(new HttpRequestException("connection refused")),
        "timeout" => FakeAuditReceiver.Failing(new TaskCanceledException("the request timed out", new TimeoutException())),
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "unknown transient failure")
    };

    [Theory]
    [InlineData("500")]
    [InlineData("503")]
    [InlineData("408")]
    [InlineData("429")]
    [InlineData("401")]
    [InlineData("404")]
    [InlineData("302")]
    [InlineData("network-failure")]
    [InlineData("timeout")]
    public async Task DrainWorker_TransientStatusCodeOrNetworkFailureOrTimeout_Retries(string transientFailure)
    {
        using var harness = new DrainHarness(TransientlyFailing(transientFailure));
        var undelivered = await harness.SpoolAsync(AuditedOperation(at: Noon));
        var spooledFileName = harness.SpooledFileNames()[0];
        using var meter = new AuditMeterTotals();

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);
        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal([undelivered.EventId, undelivered.EventId], harness.Receiver.Received.Select(envelope => envelope.EventId));
        Assert.Equal([spooledFileName], harness.SpooledFileNames());
        Assert.Empty(harness.DeadLetteredFileNames());
        Assert.Equal(2, meter.Total("audit_spool_drain_failures_total"));
    }

    [Fact]
    public async Task DrainWorker_ResponseCameFromADifferentUriThanTheEndpoint_TreatsItAsUndeliveredEvenOnA2xx()
    {
        // Defence in depth for the B09 AllowAutoRedirect=false obligation: if a redirect were
        // followed anyway, a 2xx from wherever it led must not read as "the record is stored" —
        // this worker never posted to that URI.
        using var harness = new DrainHarness(
            FakeAuditReceiver.RespondingFromADifferentUri(new Uri("https://audit.example/elsewhere"), HttpStatusCode.OK));
        await harness.SpoolAsync(AuditedOperation(at: Noon));
        var spooledFileName = harness.SpooledFileNames()[0];

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal([spooledFileName], harness.SpooledFileNames());
        Assert.Empty(harness.DeadLetteredFileNames());
    }

    [Fact]
    public async Task DrainWorker_TransientFailure_WaitsForTheOldestRecordInsteadOfSkippingPastIt()
    {
        using var harness = new DrainHarness(FakeAuditReceiver.Responding(HttpStatusCode.ServiceUnavailable));
        var oldest = await harness.SpoolAsync(AuditedOperation(at: Noon));
        await harness.SpoolAsync(AuditedOperation(at: Noon.AddMinutes(1)));

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        // Delivery is oldest-first: sending the next record while this one waits would reorder the
        // audit trail and hammer a receiver that has just said it cannot take records.
        Assert.Equal([oldest.EventId], harness.Receiver.Received.Select(envelope => envelope.EventId));
        Assert.Equal(2, harness.SpooledFileNames().Count);
    }

    [Theory]
    [InlineData(null, HealthStatus.Degraded)]
    [InlineData(HealthStatus.Unhealthy, HealthStatus.Unhealthy)]
    public async Task DrainWorker_ConsecutiveTransientFailures_HealthCheckReportsConfiguredUnreachableStatus_ClearsOnNextSuccess(
        HealthStatus? configuredStatus, HealthStatus expectedStatus)
    {
        using var harness = new DrainHarness(
            FakeAuditReceiver.Responding(
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.OK),
            options =>
            {
                if (configuredStatus is not null) options.UnreachableStatus = configuredStatus.Value;
            });
        await harness.SpoolAsync(AuditedOperation(at: Noon));

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);
        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        // One failed attempt, or two, is a blip; the endpoint is only reported unreachable once the
        // worker's own deliveries have kept failing (ADR 0019 D4).
        Assert.Equal(HealthStatus.Healthy, await HealthOf(harness));

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, await HealthOf(harness));
        Assert.NotEmpty(harness.Logger.MessagesAt(LogLevel.Warning));

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, await HealthOf(harness));
        Assert.Empty(harness.SpooledFileNames());
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 30)]
    [InlineData(12, 30)]
    public void DrainWorker_RetryDelay_DoublesWithEachConsecutiveFailureUpToTheCap(int consecutiveFailures, int expectedSeconds)
    {
        var options = new EncryptedSpoolOptions
        {
            DrainInterval = TimeSpan.FromSeconds(5),
            MaxDrainBackoff = TimeSpan.FromSeconds(30)
        };

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), DrainWorker.RetryDelayFor(consecutiveFailures, options));
    }

    [Fact]
    public void DrainWorker_RetryDelay_HugeConsecutiveFailuresWithADrainIntervalThatWouldOverflowByMultiplyingFirst_ClampsToTheCapWithoutThrowing()
    {
        // DrainInterval * 2^consecutiveFailures overflows TimeSpan's range for a legal configuration
        // (a 20-minute interval, ~2.5h of consecutive failures) long before consecutiveFailures gets
        // anywhere near where that would be surprising. The cap must be applied without ever forming
        // that intermediate value.
        var options = new EncryptedSpoolOptions
        {
            DrainInterval = TimeSpan.FromMinutes(20),
            MaxDrainBackoff = TimeSpan.FromMinutes(5)
        };

        Assert.Equal(TimeSpan.FromMinutes(5), DrainWorker.RetryDelayFor(int.MaxValue, options));
    }

    [Fact]
    public async Task DrainWorker_CrashBetweenAcceptAndDelete_RecordSurvivesForRedelivery()
    {
        // The crash is modelled as the confirmation never arriving: the receiver has the record,
        // this side never learns it, so the spool file stays and the record is sent again. That
        // duplicate is why deduplication by eventId is the receiver's job (ADR 0020 D7).
        using var harness = new DrainHarness(new FakeAuditReceiver((requestIndex, _) => requestIndex == 0
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException("the connection dropped after the record was stored"))
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        var accepted = await harness.SpoolAsync(AuditedOperation(at: Noon));

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal([accepted.EventId], harness.Receiver.Received.Select(envelope => envelope.EventId));
        Assert.NotEmpty(harness.SpooledFileNames());

        await harness.StartedOver().DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal([accepted.EventId, accepted.EventId], harness.Receiver.Received.Select(envelope => envelope.EventId));
        Assert.Empty(harness.SpooledFileNames());
        Assert.Empty(harness.DeadLetteredFileNames());
    }

    [Fact]
    public async Task DrainWorker_EmptyDeadLetterDirectory_LeavesTheHealthCheckAlone()
    {
        using var harness = new DrainHarness(FakeAuditReceiver.Responding(HttpStatusCode.OK));
        await harness.SpoolAsync(AuditedOperation(at: Noon));

        await harness.Worker.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, await HealthOf(harness));
    }

    /// <summary>
    /// Waits for something the running worker does. The clock is fake, so the drain intervals are
    /// stepped by hand; the short real waits in between only give its continuations a thread.
    /// </summary>
    private static async Task Until(Func<bool> satisfied, FakeTimeProvider? steppingClock = null, TimeSpan? step = null)
    {
        for (var attempt = 0; attempt < 100 && !satisfied(); attempt++)
        {
            steppingClock?.Advance(step ?? TimeSpan.Zero);
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.True(satisfied(), "the drain worker did not get there in the time the test allows");
    }

    [Fact]
    public async Task DrainWorker_Running_DrainsAgainEveryDrainInterval()
    {
        using var harness = new DrainHarness(FakeAuditReceiver.Responding(HttpStatusCode.OK));
        await harness.SpoolAsync(AuditedOperation(at: Noon));
        await harness.Worker.StartAsync(TestContext.Current.CancellationToken);
        await Until(() => harness.Receiver.Received.Count == 1);

        var spooledWhileRunning = await harness.SpoolAsync(AuditedOperation(at: Noon.AddMinutes(1)));
        await Until(() => harness.Receiver.Received.Count == 2, harness.Time, harness.SpoolOptions.DrainInterval);

        Assert.Equal(spooledWhileRunning.EventId, harness.Receiver.Received[1].EventId);
        Assert.Empty(harness.SpooledFileNames());
        await harness.Worker.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DrainWorker_Running_AfterAFailure_WaitsTheBackedOffDelayNotJustTheDrainInterval()
    {
        // A pure-function test of RetryDelayFor alone would still pass if the running loop ignored
        // it and always waited a plain DrainInterval — this drives the loop itself and pins the one
        // clock advance that must not be enough.
        using var harness = new DrainHarness(
            FakeAuditReceiver.Responding(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK),
            options =>
            {
                options.DrainInterval = TimeSpan.FromSeconds(5);
                options.MaxDrainBackoff = TimeSpan.FromMinutes(5);
            });
        await harness.SpoolAsync(AuditedOperation(at: Noon));
        await harness.Worker.StartAsync(TestContext.Current.CancellationToken);
        await Until(() => harness.Receiver.Received.Count == 1);

        // Until(...) returns as soon as the request is recorded, which can be before the worker has
        // classified the failure and registered its Task.Delay on the fake clock. Give it a beat so
        // the first Advance below lands after the timer exists, not before.
        await Task.Delay(20, TestContext.Current.CancellationToken);

        // One failure backs the wait off to DrainInterval * 2 = 10s: one DrainInterval of clock is
        // not enough to trigger the retry.
        harness.Time.Advance(harness.SpoolOptions.DrainInterval);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(1, harness.Receiver.Received.Count);

        harness.Time.Advance(harness.SpoolOptions.DrainInterval);
        await Until(() => harness.Receiver.Received.Count == 2);

        await harness.Worker.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DrainWorker_HostStopping_DrainsWhatIsStillSpooledBeforeItGoes()
    {
        using var harness = new DrainHarness(FakeAuditReceiver.Responding(HttpStatusCode.OK));
        await harness.SpoolAsync(AuditedOperation(at: Noon));
        await harness.Worker.StartAsync(TestContext.Current.CancellationToken);

        // Once the first cycle is through, the loop is parked on a clock that only the test moves,
        // so anything delivered from here on is the shutdown drain's doing.
        await Until(() => harness.Receiver.Received.Count == 1);
        await harness.SpoolAsync(AuditedOperation(at: Noon.AddMinutes(1)));
        await harness.SpoolAsync(AuditedOperation(at: Noon.AddMinutes(2)));

        await harness.Worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, harness.Receiver.Received.Count);
        Assert.Empty(harness.SpooledFileNames());
    }

    [Fact]
    public async Task DrainWorker_HostStopping_WhileTheEndpointDoesNotAnswer_GivesUpAtTheShutdownTimeout()
    {
        using var harness = new DrainHarness(FakeAuditReceiver.Hanging());
        await harness.SpoolAsync(AuditedOperation(at: Noon));
        await harness.Worker.StartAsync(TestContext.Current.CancellationToken);
        await Until(() => harness.Receiver.Received.Count == 1);

        var stopping = harness.Worker.StopAsync(TestContext.Current.CancellationToken);
        await Until(() => harness.Receiver.Received.Count == 2);
        harness.Time.Advance(harness.SpoolOptions.ShutdownDrainTimeout);

        await stopping;

        // Shutdown is bounded: an endpoint that never answers holds nothing up, and the record is
        // still on disk for the next start.
        Assert.Single(harness.SpooledFileNames());
    }

    [Fact]
    public async Task DrainWorker_CycleFailsUnexpectedly_KeepsRunningAndDrainsOnTheNextOne()
    {
        // An unhandled exception out of ExecuteAsync stops the host: an audit delivery problem
        // would take the whole application down with it.
        using var harness = new DrainHarness(new FakeAuditReceiver((requestIndex, _) => requestIndex == 0
            ? Task.FromException<HttpResponseMessage>(new InvalidOperationException("something no delivery rule covers"))
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        await harness.SpoolAsync(AuditedOperation(at: Noon));
        using var meter = new AuditMeterTotals();

        await harness.Worker.StartAsync(TestContext.Current.CancellationToken);
        await Until(() => harness.SpooledFileNames().Count == 0, harness.Time, harness.SpoolOptions.DrainInterval);

        Assert.NotEmpty(harness.Logger.MessagesAt(LogLevel.Error));

        // Not just a log line: a swallowed cycle failure must move a counter too, so a loop stuck
        // failing every cycle is something an alert can catch without matching log strings.
        Assert.True(meter.Total("audit_spool_drain_failures_total") >= 1);
        await harness.Worker.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DrainWorker_Running_WithANonPositiveDrainInterval_LogsInsteadOfFaultingTheHost()
    {
        // Nothing validates DrainInterval/MaxDrainBackoff as positive yet (B09's job); until it
        // does, a bad value must not escape ExecuteAsync and take the whole application down with
        // it, the same guarantee the class already gives every delivery-path exception.
        using var harness = new DrainHarness(
            FakeAuditReceiver.Responding(HttpStatusCode.OK),
            options =>
            {
                options.DrainInterval = TimeSpan.FromSeconds(-1);
                options.MaxDrainBackoff = TimeSpan.FromSeconds(-1);
            });

        await harness.Worker.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await harness.Worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(harness.Logger.MessagesAt(LogLevel.Error));
        Assert.NotNull(harness.Worker.ExecuteTask);
        Assert.False(harness.Worker.ExecuteTask.IsFaulted);
    }
}
