using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

public class ConfigureOptionsOverloadTests
{
    private static HostApplicationBuilder NewBuilder()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SerilogStepUp:EnableOtlpExporter"] = "false",
        });
        return builder;
    }

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
}
