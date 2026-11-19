using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using static Lukdrasil.StepUpLogging.Tests.TestHosts;

namespace Lukdrasil.StepUpLogging.Tests;

[Collection(OtlpEnvironmentCollection.Name)]
public class ConfigureOptionsOverloadTests
{
    [Fact]
    public void CombinedOverload_AppliesOptionsAndRunsConfigureCallback()
    {
        var configureRan = false;
        var builder = NewBuilder();
        builder.AddStepUpLogging(
            opts => opts.DurationSeconds = 42,
            (_, _) => configureRan = true);

        using var host = builder.Build();
        host.Services.GetRequiredService<ILoggerFactory>();

        Assert.Equal(42, host.Services.GetRequiredService<IOptions<StepUpLoggingOptions>>().Value.DurationSeconds);
        Assert.True(configureRan);
    }

    [Fact]
    public void ExistingOverloads_StillResolveFromASingleLambda()
    {
        var optionsBuilder = NewBuilder();
        optionsBuilder.AddStepUpLogging(opts => opts.DurationSeconds = 7);
        using var optionsHost = optionsBuilder.Build();

        var configureRan = false;
        var configureBuilder = NewBuilder();
        configureBuilder.AddStepUpLogging((_, _) => configureRan = true);
        using var configureHost = configureBuilder.Build();
        configureHost.Services.GetRequiredService<ILoggerFactory>();

        Assert.Equal(7, optionsHost.Services.GetRequiredService<IOptions<StepUpLoggingOptions>>().Value.DurationSeconds);
        Assert.True(configureRan);
    }

    [Fact]
    public void OtlpExporterDisabledInCode_DoesNotRegisterTheOpenTelemetrySdk()
    {
        using var endpoint = new OtlpEndpoint();
        var builder = Host.CreateApplicationBuilder();
        builder.AddStepUpLogging(opts => opts.EnableOtlpExporter = false);

        Assert.DoesNotContain(builder.Services, IsOtlpExporterRegistration);
    }

    [Fact]
    public void OpenTelemetryRegisteredBeforehand_IsLeftToTheApp()
    {
        using var endpoint = new OtlpEndpoint();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddOpenTelemetry();
        builder.AddStepUpLogging();

        Assert.DoesNotContain(builder.Services, IsOtlpExporterRegistration);
    }

    private static bool IsOtlpExporterRegistration(ServiceDescriptor sd)
        => sd.ServiceType.FullName == "OpenTelemetry.Exporter.UseOtlpExporterRegistration";

    // With the endpoint set, EnableOtlpExporter defaults to true, so only the case under test keeps the SDK out.
    private sealed class OtlpEndpoint : IDisposable
    {
        private const string Name = "OTEL_EXPORTER_OTLP_ENDPOINT";
        private readonly string? _previous = Environment.GetEnvironmentVariable(Name);

        public OtlpEndpoint() => Environment.SetEnvironmentVariable(Name, "http://127.0.0.1:4317");

        public void Dispose() => Environment.SetEnvironmentVariable(Name, _previous);
    }
}
