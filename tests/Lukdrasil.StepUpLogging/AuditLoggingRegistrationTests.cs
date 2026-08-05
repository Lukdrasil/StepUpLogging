using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

        public ValueTask WriteAsync(AuditEvent auditEvent)
        {
            Written.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
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
        await auditLogger.AuditAsync(AuditEvent.Success("order.cancel", "user-42"));

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
    public async Task WithoutAddStepUpLogging_TheHostStartsCleanly_AndTheFirstResolutionThrows()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddAuditLogging<TestAuditSink>();

        using var host = builder.Build();

        // The dependency on AddStepUpLogging is structural, not validated at start-up: an app that
        // forgot it boots normally and fails on its first audited operation.
        await host.StartAsync(TestContext.Current.CancellationToken);

        using var scope = host.Services.CreateScope();
        var thrown = Assert.Throws<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<IAuditLogger<AuditLoggingRegistrationTests>>());
        Assert.Contains(nameof(CompiledRedactionPatterns), thrown.Message);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}
