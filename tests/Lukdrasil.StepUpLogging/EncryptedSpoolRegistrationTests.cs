using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// DI wiring of <see cref="StepUpLoggingEncryptedSpoolExtensions.AddEncryptedSpoolAuditSink"/>:
/// what it registers, how it fails when a required option is missing, and the hand-off items B07
/// and B08 left for it (a single shared <see cref="SpoolWriter"/>, a redirect-disabled delivery
/// client, a health check under the <c>"audit"</c> tag).
/// </summary>
public class EncryptedSpoolRegistrationTests : IDisposable
{
    // AddStepUpLogging assigns Serilog's static Log.Logger; restore it so the tests stay isolated.
    private readonly Serilog.ILogger _previousLogger = Log.Logger;

    public void Dispose() => Log.Logger = _previousLogger;

    private static HostApplicationBuilder CreateHostBuilder()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SerilogStepUp:EnableOtlpExporter"] = "false",
            ["SerilogStepUp:EnablePreErrorBuffering"] = "false",
        });
        return builder;
    }

    /// <summary>Every option required to start; a test that wants one invalid overrides it after this.</summary>
    private static void ValidOptions(EncryptedSpoolOptions options, string spoolDirectory)
    {
        options.SpoolDirectory = spoolDirectory;
        options.EndpointBaseUrl = "https://audit.example/api";
        options.ModuleName = "orders-api";
        options.Version = "4.0.0";
    }

    /// <summary>A fully wired, valid host: AddStepUpLogging + AddEncryptedSpoolAuditSink + the consumer's encryptor.</summary>
    private static HostApplicationBuilder ValidHostBuilder(TempSpoolDirectory spool)
    {
        var builder = CreateHostBuilder();
        builder.AddStepUpLogging();
        builder.Services.AddSingleton<IAuditPayloadEncryptor, FakeAuditPayloadEncryptor>();
        builder.AddEncryptedSpoolAuditSink(options => ValidOptions(options, spool.FullPath));
        return builder;
    }

    [Fact]
    public async Task AddEncryptedSpoolAuditSink_ValidOptions_HostStarts_AndSinkWorkerHealthCheckResolve()
    {
        using var spool = new TempSpoolDirectory();
        using var host = ValidHostBuilder(spool).Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.IsType<EncryptedSpoolAuditSink>(host.Services.GetRequiredService<IAuditEventSink>());
            Assert.Contains(host.Services.GetServices<IHostedService>(), service => service is DrainWorker);

            var registrations = host.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
            var registration = Assert.Single(registrations, r => r.Name == StepUpLoggingEncryptedSpoolExtensions.HealthCheckName);
            Assert.Contains(StepUpLoggingEncryptedSpoolExtensions.HealthCheckTag, registration.Tags);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Each case leaves exactly one option invalid, so the assertion isolates that option's own message.</summary>
    public static IEnumerable<object[]> InvalidOptionCases() =>
    [
        Case(o => o.SpoolDirectory = "", nameof(EncryptedSpoolOptions.SpoolDirectory)),
        Case(o => o.EndpointBaseUrl = "", nameof(EncryptedSpoolOptions.EndpointBaseUrl)),
        Case(o => o.EndpointBaseUrl = "not-a-url", nameof(EncryptedSpoolOptions.EndpointBaseUrl)),
        Case(o => o.EndpointBaseUrl = "/relative/audit", nameof(EncryptedSpoolOptions.EndpointBaseUrl)),
        Case(o => o.EndpointBaseUrl = "https://audit.example/api?tenant=1", nameof(EncryptedSpoolOptions.EndpointBaseUrl)),
        Case(o => o.EndpointBaseUrl = "https://audit.example/api#section", nameof(EncryptedSpoolOptions.EndpointBaseUrl)),
        Case(o => o.ModuleName = "", nameof(EncryptedSpoolOptions.ModuleName)),
        Case(o => o.Version = "", nameof(EncryptedSpoolOptions.Version)),
        Case(o => o.MaxPayloadBytes = 0, nameof(EncryptedSpoolOptions.MaxPayloadBytes)),
        Case(o => o.SpoolMaxBytes = 0, nameof(EncryptedSpoolOptions.SpoolMaxBytes)),
        Case(o => o.SpoolMaxEntries = 0, nameof(EncryptedSpoolOptions.SpoolMaxEntries)),
        Case(o => o.DrainInterval = TimeSpan.Zero, nameof(EncryptedSpoolOptions.DrainInterval)),
        Case(o => o.MaxDrainBackoff = TimeSpan.Zero, nameof(EncryptedSpoolOptions.MaxDrainBackoff)),
        Case(o => o.ShutdownDrainTimeout = TimeSpan.Zero, nameof(EncryptedSpoolOptions.ShutdownDrainTimeout)),
        Case(o => o.UnreadableRetryLimit = 0, nameof(EncryptedSpoolOptions.UnreadableRetryLimit)),
        Case(o => o.DeliveryTimeout = TimeSpan.Zero, nameof(EncryptedSpoolOptions.DeliveryTimeout)),
        Case(o => o.DeliveryTimeout = TimeSpan.FromSeconds(-5), nameof(EncryptedSpoolOptions.DeliveryTimeout)),
    ];

    private static object[] Case(Action<EncryptedSpoolOptions> invalidate, string optionName) => [invalidate, optionName];

    [Theory]
    [MemberData(nameof(InvalidOptionCases))]
    public async Task AddEncryptedSpoolAuditSink_InvalidOption_RefusesToStart_NamingTheOption(
        Action<EncryptedSpoolOptions> invalidate, string expectedOptionName)
    {
        using var spool = new TempSpoolDirectory();
        var builder = CreateHostBuilder();
        builder.AddStepUpLogging();
        builder.Services.AddSingleton<IAuditPayloadEncryptor, FakeAuditPayloadEncryptor>();
        builder.AddEncryptedSpoolAuditSink(options =>
        {
            ValidOptions(options, spool.FullPath);
            invalidate(options);
        });

        using var host = builder.Build();

        var thrown = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(expectedOptionName, string.Join(' ', thrown.Failures));
    }

    /// <summary>
    /// The positive control for every case above: proves the host actually refuses to <em>start</em>
    /// rather than merely rejecting the options object on construction — a hosted service that ran
    /// would mean a business operation could reach the (unwritable, misconfigured) sink first.
    /// </summary>
    [Fact]
    public async Task AddEncryptedSpoolAuditSink_MissingPrerequisite_RefusesToStart_BeforeAnyHostedServiceRuns()
    {
        var builder = CreateHostBuilder();
        var serverStub = new StartupRecordingService();
        builder.Services.AddSingleton<IHostedService>(serverStub);
        builder.AddStepUpLogging();
        builder.Services.AddSingleton<IAuditPayloadEncryptor, FakeAuditPayloadEncryptor>();
        builder.AddEncryptedSpoolAuditSink(options => ValidOptions(options, spoolDirectory: ""));

        using var host = builder.Build();

        await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.False(serverStub.Started);
    }

    /// <summary>
    /// The positive control for the two <see cref="EncryptedSpoolOptions.DeliveryTimeout"/> cases in
    /// <see cref="InvalidOptionCases"/>: <see cref="Timeout.InfiniteTimeSpan"/> (also &lt;= zero) must
    /// not be rejected, since <see cref="HttpClient.Timeout"/> treats it as "never time out", not
    /// "already timed out".
    /// </summary>
    [Fact]
    public async Task AddEncryptedSpoolAuditSink_DeliveryTimeoutIsInfinite_HostStarts()
    {
        using var spool = new TempSpoolDirectory();
        var builder = CreateHostBuilder();
        builder.AddStepUpLogging();
        builder.Services.AddSingleton<IAuditPayloadEncryptor, FakeAuditPayloadEncryptor>();
        builder.AddEncryptedSpoolAuditSink(options =>
        {
            ValidOptions(options, spool.FullPath);
            options.DeliveryTimeout = Timeout.InfiniteTimeSpan;
        });

        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Proves start-up sink resolution (B07 hand-off item 4) is load-bearing: a
    /// <see cref="EncryptedSpoolOptions.SpoolDirectory"/> that passes every string/URI rule but
    /// cannot physically be created (its parent path component is a file) must still fail the host at
    /// start, not on the first audited write. Deleting the forced <c>GetRequiredService&lt;IAuditEventSink&gt;()</c>
    /// call would make this test hang or pass wrongly instead of failing here.
    /// </summary>
    [Fact]
    public async Task AddEncryptedSpoolAuditSink_SpoolDirectoryCannotBeCreated_RefusesToStart_BeforeAnyHostedServiceRuns()
    {
        using var root = new TempSpoolDirectory();
        var blockingFile = Path.Combine(root.FullPath, "not-a-directory");
        File.WriteAllText(blockingFile, "x");
        var unusableSpoolDirectory = Path.Combine(blockingFile, "spool");

        var builder = CreateHostBuilder();
        var serverStub = new StartupRecordingService();
        builder.Services.AddSingleton<IHostedService>(serverStub);
        builder.AddStepUpLogging();
        builder.Services.AddSingleton<IAuditPayloadEncryptor, FakeAuditPayloadEncryptor>();
        builder.AddEncryptedSpoolAuditSink(options => ValidOptions(options, unusableSpoolDirectory));

        using var host = builder.Build();

        await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.False(serverStub.Started);
    }

    /// <summary>
    /// Proves the named client obligations (B08 hand-off item 1) beyond the redirect flag: nothing
    /// else asserted that <see cref="EncryptedSpoolOptions.DeliveryTimeout"/> and
    /// <see cref="EncryptedSpoolOptions.ConfigureProducerCredentials"/> actually reach the client —
    /// deleting either assignment in <c>AddDeliveryHttpClient</c> would still leave the suite green
    /// without this test.
    /// </summary>
    [Fact]
    public void AddEncryptedSpoolAuditSink_NamedHttpClient_HasConfiguredTimeoutAndProducerCredentials()
    {
        using var spool = new TempSpoolDirectory();
        var builder = CreateHostBuilder();
        builder.AddStepUpLogging();
        builder.Services.AddSingleton<IAuditPayloadEncryptor, FakeAuditPayloadEncryptor>();
        builder.AddEncryptedSpoolAuditSink(options =>
        {
            ValidOptions(options, spool.FullPath);
            options.DeliveryTimeout = TimeSpan.FromSeconds(7);
            options.ConfigureProducerCredentials = client => client.DefaultRequestHeaders.Add("X-Api-Key", "secret-value");
        });

        using var host = builder.Build();
        var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient(DrainWorker.HttpClientName);

        Assert.Equal(TimeSpan.FromSeconds(7), client.Timeout);
        Assert.Equal("secret-value", Assert.Single(client.DefaultRequestHeaders.GetValues("X-Api-Key")));
    }

    /// <summary>
    /// A second <c>AddEncryptedSpoolAuditSink</c> call must name itself, not the
    /// <c>AddAuditLogging&lt;EncryptedSpoolAuditSink&gt;</c> it calls internally — the caller never
    /// invoked that method, and its remedy (<c>RemoveAll&lt;IAuditEventSink&gt;()</c>) does not fit
    /// "you called this method twice".
    /// </summary>
    [Fact]
    public void AddEncryptedSpoolAuditSink_CalledTwice_ThrowsNamingItself()
    {
        using var spool = new TempSpoolDirectory();
        var builder = CreateHostBuilder();
        builder.AddStepUpLogging();
        builder.AddEncryptedSpoolAuditSink(options => ValidOptions(options, spool.FullPath));

        var ex = Assert.Throws<InvalidOperationException>(
            () => builder.AddEncryptedSpoolAuditSink(options => ValidOptions(options, spool.FullPath)));

        Assert.Contains(nameof(StepUpLoggingEncryptedSpoolExtensions.AddEncryptedSpoolAuditSink), ex.Message);
    }

    [Fact]
    public void AddEncryptedSpoolAuditSink_ThenAddAuditLogging_Throws()
    {
        using var spool = new TempSpoolDirectory();
        var builder = CreateHostBuilder();
        builder.AddStepUpLogging();
        builder.AddEncryptedSpoolAuditSink(options => ValidOptions(options, spool.FullPath));

        var ex = Assert.Throws<InvalidOperationException>(
            () => builder.AddAuditLogging<ReplacementAuditSink>());

        Assert.Contains(nameof(EncryptedSpoolAuditSink), ex.Message);
        Assert.Contains(nameof(ReplacementAuditSink), ex.Message);
    }

    [Fact]
    public void AddAuditLogging_ThenAddEncryptedSpoolAuditSink_Throws()
    {
        using var spool = new TempSpoolDirectory();
        var builder = CreateHostBuilder();
        builder.AddStepUpLogging();
        builder.AddAuditLogging<ReplacementAuditSink>();

        var ex = Assert.Throws<InvalidOperationException>(
            () => builder.AddEncryptedSpoolAuditSink(options => ValidOptions(options, spool.FullPath)));

        Assert.Contains(nameof(EncryptedSpoolAuditSink), ex.Message);
        Assert.Contains(nameof(ReplacementAuditSink), ex.Message);
    }

    [Fact]
    public void AddEncryptedSpoolAuditSink_NamedHttpClient_HasAutoRedirectDisabled()
    {
        using var spool = new TempSpoolDirectory();
        using var host = ValidHostBuilder(spool).Build();

        var handlerFactory = host.Services.GetRequiredService<IHttpMessageHandlerFactory>();
        var handler = handlerFactory.CreateHandler(DrainWorker.HttpClientName);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        var socketsHandler = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(socketsHandler.AllowAutoRedirect);
    }

    [Fact]
    public void AddEncryptedSpoolAuditSink_SpoolWriter_ResolvesToTheSameInstanceEveryTime()
    {
        using var spool = new TempSpoolDirectory();
        using var host = ValidHostBuilder(spool).Build();

        var first = host.Services.GetRequiredService<SpoolWriter>();
        var second = host.Services.GetRequiredService<SpoolWriter>();

        Assert.Same(first, second);
    }

    /// <summary>A record of its own, so every write lands on a spool file of its own.</summary>
    private static AuditEvent SpoolableEvent() =>
        AuditEvent.Success("order.cancel", "user-42", "user") with
        {
            EventId = Guid.CreateVersion7(),
            TimestampUtc = DateTimeOffset.UtcNow
        };

    /// <summary>
    /// A single reading of <paramref name="instrumentName"/> on the shared <c>StepUpLogging.Audit</c>
    /// meter. An <c>ObservableGauge</c> instrument has no <c>Dispose</c>, so every host any test in
    /// this process ever started stays registered and keeps reporting its own (frozen, once that host
    /// is gone) value — a pre-existing, accepted leak (notes.md). Callers isolate their own host's
    /// contribution by reading before and after the change they are asserting on, rather than
    /// trusting an absolute total.
    /// </summary>
    private static long GaugeTotal(string instrumentName)
    {
        using var meter = new AuditMeterTotals();
        meter.RecordObservableInstruments();
        return meter.Total(instrumentName);
    }

    [Fact]
    public async Task AddEncryptedSpoolAuditSink_SpoolDepthAndBytesGauges_ReflectWrites()
    {
        using var spool = new TempSpoolDirectory();
        using var host = ValidHostBuilder(spool).Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            // Baseline after this host's own gauge exists (an empty spool) but before it writes
            // anything, so the delta below isolates this host's contribution from any other gauge
            // still registered on the shared meter (see GaugeTotal's doc).
            var depthBefore = GaugeTotal("audit_spool_depth");
            var bytesBefore = GaugeTotal("audit_spool_bytes");

            var sink = host.Services.GetRequiredService<IAuditEventSink>();
            await sink.WriteAsync(SpoolableEvent());
            await sink.WriteAsync(SpoolableEvent());

            Assert.Equal(2, GaugeTotal("audit_spool_depth") - depthBefore);
            Assert.True(GaugeTotal("audit_spool_bytes") - bytesBefore > 0);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// A restart sitting on an undelivered backlog is exactly the case the gauge exists for: it must
    /// not read 0 just because this process has not written anything yet. Plants the files with a
    /// throwaway <see cref="SpoolWriter"/> before the host under test ever sees the directory, so the
    /// gauge's first-ever observation is also the tracker's first-ever touch.
    /// </summary>
    [Fact]
    public async Task AddEncryptedSpoolAuditSink_SpoolDepthGauge_ReflectsFilesAlreadyOnDiskAtStart_BeforeAnyWrite()
    {
        using var spool = new TempSpoolDirectory();
        var priorProcess = new SpoolWriter(spool.FullPath);
        await priorProcess.WriteAsync(new SpoolEnvelope { EventId = Guid.CreateVersion7(), CreatedUtc = DateTimeOffset.UtcNow, Payload = [1, 2, 3] });
        await priorProcess.WriteAsync(new SpoolEnvelope { EventId = Guid.CreateVersion7(), CreatedUtc = DateTimeOffset.UtcNow, Payload = [4, 5, 6] });

        // Baseline before this host (and its gauge) exists at all, so the delta below isolates it
        // from any other gauge still registered on the shared meter (see GaugeTotal's doc).
        var depthBefore = GaugeTotal("audit_spool_depth");

        using var host = ValidHostBuilder(spool).Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(2, GaugeTotal("audit_spool_depth") - depthBefore);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void EncryptedSpoolOptions_RequiredMembers_AreEndpointCredentialsModuleNameVersion_NoKeyOptions()
    {
        var propertyNames = typeof(EncryptedSpoolOptions).GetProperties().Select(p => p.Name).ToArray();

        Assert.Contains(nameof(EncryptedSpoolOptions.EndpointBaseUrl), propertyNames);
        Assert.Contains(nameof(EncryptedSpoolOptions.ConfigureProducerCredentials), propertyNames);
        Assert.Contains(nameof(EncryptedSpoolOptions.ModuleName), propertyNames);
        Assert.Contains(nameof(EncryptedSpoolOptions.Version), propertyNames);

        // Encryption and all key handling live entirely inside the consumer's IAuditPayloadEncryptor
        // (ADR 0017 D3) — a JWKS URL, a key TTL, or a cache path must never sneak onto this type.
        string[] keyRelatedSubstrings = ["Key", "Jwks", "Algorithm", "Secret", "Cache", "Ttl"];
        Assert.DoesNotContain(propertyNames, name => keyRelatedSubstrings.Any(name.Contains));
    }

    private sealed class ReplacementAuditSink : IAuditEventSink
    {
        public ValueTask<AuditWriteResult> WriteAsync(AuditEvent auditEvent) =>
            ValueTask.FromResult(AuditWriteResult.Stored);
    }

    private sealed class StartupRecordingService : IHostedService
    {
        public bool Started { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
