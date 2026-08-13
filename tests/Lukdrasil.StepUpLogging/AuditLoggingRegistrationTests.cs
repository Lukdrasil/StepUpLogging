using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// DI wiring of <see cref="StepUpLoggingExtensions.AddAuditLogging{TSink}"/>: what it registers,
/// with which lifetimes, and how it fails when a prerequisite is missing.
/// </summary>
public class AuditLoggingRegistrationTests : IDisposable
{
    // AddStepUpLogging assigns Serilog's static Log.Logger; restore it so the tests stay isolated.
    private readonly Serilog.ILogger _previousLogger = Log.Logger;

    public void Dispose() => Log.Logger = _previousLogger;

    private sealed class TestAuditSink : IAuditEventSink
    {
        public List<AuditEvent> Written { get; } = [];

        public ValueTask<AuditWriteResult> WriteAsync(AuditEvent auditEvent)
        {
            Written.Add(auditEvent);
            return ValueTask.FromResult(AuditWriteResult.Stored);
        }
    }

    /// <summary>A second, distinct sink type so a double-registration test can prove both names appear.</summary>
    private sealed class SecondTestAuditSink : IAuditEventSink
    {
        public ValueTask<AuditWriteResult> WriteAsync(AuditEvent auditEvent)
            => ValueTask.FromResult(AuditWriteResult.Stored);
    }

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

    /// <summary>Builds a fully wired host; <paramref name="sinkLifetime"/> stays unspecified unless a test is about it.</summary>
    private static IHost BuildAuditingHost(ServiceLifetime? sinkLifetime = null)
    {
        var builder = CreateHostBuilder();
        builder.AddStepUpLogging();

        if (sinkLifetime is null)
        {
            builder.AddAuditLogging<TestAuditSink>();
        }
        else
        {
            builder.AddAuditLogging<TestAuditSink>(sinkLifetime.Value);
        }

        return builder.Build();
    }

    [Fact]
    public void AddAuditLogging_RegistersTheSinkAndTheAuditLogger()
    {
        using var host = BuildAuditingHost();
        using var scope = host.Services.CreateScope();

        Assert.IsType<TestAuditSink>(scope.ServiceProvider.GetRequiredService<IAuditEventSink>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAuditLogger<AuditLoggingRegistrationTests>>());
        Assert.NotNull(scope.ServiceProvider.GetService<IHttpContextAccessor>());
    }

    [Fact]
    public async Task AddAuditLogging_ResolvedAuditLogger_WritesToTheRegisteredSink()
    {
        using var host = BuildAuditingHost();
        using var scope = host.Services.CreateScope();

        var auditLogger = scope.ServiceProvider.GetRequiredService<IAuditLogger<AuditLoggingRegistrationTests>>();
        await auditLogger.AuditAsync(AuditEvent.Success("order.cancel", "user-42", "user"));

        var sink = (TestAuditSink)scope.ServiceProvider.GetRequiredService<IAuditEventSink>();
        Assert.Equal("order.cancel", Assert.Single(sink.Written).Action);
    }

    [Fact]
    public void AddAuditLogging_RegistersTheSinkAsScoped_ByDefault()
    {
        using var host = BuildAuditingHost();
        using var firstScope = host.Services.CreateScope();
        using var secondScope = host.Services.CreateScope();

        var first = firstScope.ServiceProvider.GetRequiredService<IAuditEventSink>();

        Assert.Same(first, firstScope.ServiceProvider.GetRequiredService<IAuditEventSink>());
        Assert.NotSame(first, secondScope.ServiceProvider.GetRequiredService<IAuditEventSink>());
    }

    [Fact]
    public void AddAuditLogging_HonoursAnExplicitSinkLifetime()
    {
        using var host = BuildAuditingHost(ServiceLifetime.Singleton);
        using var firstScope = host.Services.CreateScope();
        using var secondScope = host.Services.CreateScope();

        Assert.Same(
            firstScope.ServiceProvider.GetRequiredService<IAuditEventSink>(),
            secondScope.ServiceProvider.GetRequiredService<IAuditEventSink>());
    }

    [Fact]
    public void AddAuditLogging_RegistersTheAuditLoggerAsScoped_EvenForASingletonSink()
    {
        using var host = BuildAuditingHost(ServiceLifetime.Singleton);
        using var firstScope = host.Services.CreateScope();
        using var secondScope = host.Services.CreateScope();

        var first = firstScope.ServiceProvider.GetRequiredService<IAuditLogger<AuditLoggingRegistrationTests>>();

        // Both halves are needed: "different across scopes" alone is equally true of Transient.
        Assert.Same(first, firstScope.ServiceProvider.GetRequiredService<IAuditLogger<AuditLoggingRegistrationTests>>());
        Assert.NotSame(first, secondScope.ServiceProvider.GetRequiredService<IAuditLogger<AuditLoggingRegistrationTests>>());
    }

    [Fact]
    public void WithoutAddAuditLogging_TheAuditLoggerIsAbsentFromTheContainer()
    {
        var builder = CreateHostBuilder();
        builder.AddStepUpLogging();

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();

        // No default and no no-op sink exists to fall back on: audit is off unless it was asked for.
        Assert.Null(scope.ServiceProvider.GetService<IAuditEventSink>());
        Assert.Throws<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<IAuditLogger<AuditLoggingRegistrationTests>>());
    }

    [Fact]
    public async Task WithoutAddStepUpLogging_TheHostFailsToStart_NamingBothMethods()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddAuditLogging<TestAuditSink>();

        using var host = builder.Build();

        var thrown = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        var message = Assert.Single(thrown.Failures);
        Assert.Contains(nameof(StepUpLoggingExtensions.AddAuditLogging), message);
        Assert.Contains(nameof(StepUpLoggingExtensions.AddStepUpLogging), message);
    }

    [Fact]
    public async Task WithoutAddStepUpLogging_TheHostFailsToStart_BeforeAnyHostedServiceRuns()
    {
        var (host, serverStub) = BuildHostWithServerStub(withStepUpLogging: false);
        using (host)
        {
            await Assert.ThrowsAsync<OptionsValidationException>(
                () => host.StartAsync(TestContext.Current.CancellationToken));

            Assert.False(serverStub.Started);
        }
    }

    /// <summary>
    /// The positive control for the test above, whose only assertion is a negative one: were the
    /// stub to stop being a hosted service that runs at all, that assertion would hold vacuously.
    /// </summary>
    [Fact]
    public async Task WithAddStepUpLogging_TheHostStarts_AndTheSameHostedServiceRuns()
    {
        var (host, serverStub) = BuildHostWithServerStub(withStepUpLogging: true);
        using (host)
        {
            await host.StartAsync(TestContext.Current.CancellationToken);

            Assert.True(serverStub.Started);

            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// The stub stands in for Kestrel: registered before the consumer's own calls, so a check that
    /// merely queued itself behind it would let requests through before failing.
    /// </summary>
    private static (IHost Host, StartupRecordingService ServerStub) BuildHostWithServerStub(bool withStepUpLogging)
    {
        var builder = CreateHostBuilder();
        var serverStub = new StartupRecordingService();
        builder.Services.AddSingleton<IHostedService>(serverStub);
        builder.AddAuditLogging<TestAuditSink>();

        if (withStepUpLogging)
        {
            builder.AddStepUpLogging();
        }

        return (builder.Build(), serverStub);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheTwoRegistrationCalls_StartTheHost_InEitherOrder(bool auditFirst)
    {
        var builder = CreateHostBuilder();

        if (auditFirst)
        {
            builder.AddAuditLogging<TestAuditSink>();
            builder.AddStepUpLogging();
        }
        else
        {
            builder.AddStepUpLogging();
            builder.AddAuditLogging<TestAuditSink>();
        }

        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddAuditLogging_CalledTwiceWithDifferentSinks_ThrowsNamingBoth(bool testSinkFirst)
    {
        var builder = CreateHostBuilder();
        builder.AddStepUpLogging();

        var firstSinkName = testSinkFirst ? nameof(TestAuditSink) : nameof(SecondTestAuditSink);
        var secondSinkName = testSinkFirst ? nameof(SecondTestAuditSink) : nameof(TestAuditSink);

        if (testSinkFirst)
        {
            builder.AddAuditLogging<TestAuditSink>();
        }
        else
        {
            builder.AddAuditLogging<SecondTestAuditSink>();
        }

        var ex = testSinkFirst
            ? Assert.Throws<InvalidOperationException>(() => builder.AddAuditLogging<SecondTestAuditSink>())
            : Assert.Throws<InvalidOperationException>(() => builder.AddAuditLogging<TestAuditSink>());

        Assert.Contains(firstSinkName, ex.Message);
        Assert.Contains(secondSinkName, ex.Message);
    }

    /// <summary>Positive control for the guard above: a lone registration must still work.</summary>
    [Fact]
    public void AddAuditLogging_CalledOnce_StillRegistersSink()
    {
        using var host = BuildAuditingHost();
        using var scope = host.Services.CreateScope();

        Assert.IsType<TestAuditSink>(scope.ServiceProvider.GetRequiredService<IAuditEventSink>());
    }

    /// <summary>
    /// Documents the README testing recipe the ADR 0018 amendment describes: a
    /// <c>WebApplicationFactory</c>'s <c>ConfigureTestServices</c> runs before the host is built, so
    /// its call to <see cref="StepUpLoggingExtensions.AddAuditLogging{TSink}"/> lands on the same
    /// <see cref="IServiceCollection"/> Program.cs already registered a sink on, and the guard fires
    /// exactly as it would for two calls in Program.cs itself.
    /// </summary>
    [Fact]
    public void AddAuditLogging_CalledAgainAfterProgramRegisteredASink_LikeConfigureTestServicesWould_Throws()
    {
        var builder = CreateHostBuilder();
        builder.AddStepUpLogging();
        builder.AddAuditLogging<TestAuditSink>(); // Program.cs

        var ex = Assert.Throws<InvalidOperationException>(
            () => builder.AddAuditLogging<SecondTestAuditSink>()); // ConfigureTestServices

        Assert.Contains(nameof(TestAuditSink), ex.Message);
        Assert.Contains(nameof(SecondTestAuditSink), ex.Message);
        Assert.Contains($"{nameof(ServiceCollectionDescriptorExtensions.RemoveAll)}<{nameof(IAuditEventSink)}>", ex.Message);
    }

    /// <summary>Proves the recipe the guard's own message points to actually works.</summary>
    [Fact]
    public void AddAuditLogging_AfterRemoveAllOfTheExistingSink_RegistersTheReplacementSink()
    {
        var builder = CreateHostBuilder();
        builder.AddStepUpLogging();
        builder.AddAuditLogging<TestAuditSink>(); // Program.cs

        builder.Services.RemoveAll<IAuditEventSink>();
        builder.AddAuditLogging<SecondTestAuditSink>(); // ConfigureTestServices

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();

        Assert.IsType<SecondTestAuditSink>(scope.ServiceProvider.GetRequiredService<IAuditEventSink>());
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
