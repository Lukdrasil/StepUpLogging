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
    public static IEnumerable<object[]> InvalidOptionCases()
    {
        yield return [Invalidate(o => o.SpoolDirectory = ""), nameof(EncryptedSpoolOptions.SpoolDirectory)];
        yield return [Invalidate(o => o.EndpointBaseUrl = ""), nameof(EncryptedSpoolOptions.EndpointBaseUrl)];
        yield return [Invalidate(o => o.EndpointBaseUrl = "not-a-url"), nameof(EncryptedSpoolOptions.EndpointBaseUrl)];
        yield return [Invalidate(o => o.EndpointBaseUrl = "/relative/audit"), nameof(EncryptedSpoolOptions.EndpointBaseUrl)];
        yield return [Invalidate(o => o.EndpointBaseUrl = "https://audit.example/api?tenant=1"), nameof(EncryptedSpoolOptions.EndpointBaseUrl)];
        yield return [Invalidate(o => o.EndpointBaseUrl = "https://audit.example/api#section"), nameof(EncryptedSpoolOptions.EndpointBaseUrl)];
        yield return [Invalidate(o => o.ModuleName = ""), nameof(EncryptedSpoolOptions.ModuleName)];
        yield return [Invalidate(o => o.Version = ""), nameof(EncryptedSpoolOptions.Version)];
        yield return [Invalidate(o => o.MaxPayloadBytes = 0), nameof(EncryptedSpoolOptions.MaxPayloadBytes)];
        yield return [Invalidate(o => o.SpoolMaxBytes = 0), nameof(EncryptedSpoolOptions.SpoolMaxBytes)];
        yield return [Invalidate(o => o.SpoolMaxEntries = 0), nameof(EncryptedSpoolOptions.SpoolMaxEntries)];
        yield return [Invalidate(o => o.DrainInterval = TimeSpan.Zero), nameof(EncryptedSpoolOptions.DrainInterval)];
        yield return [Invalidate(o => o.MaxDrainBackoff = TimeSpan.Zero), nameof(EncryptedSpoolOptions.MaxDrainBackoff)];
        yield return [Invalidate(o => o.ShutdownDrainTimeout = TimeSpan.Zero), nameof(EncryptedSpoolOptions.ShutdownDrainTimeout)];
        yield return [Invalidate(o => o.UnreadableRetryLimit = 0), nameof(EncryptedSpoolOptions.UnreadableRetryLimit)];
    }

    private static Action<EncryptedSpoolOptions> Invalidate(Action<EncryptedSpoolOptions> invalidate) => invalidate;

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
