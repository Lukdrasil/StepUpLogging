using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

public class PrefixFilteredConfigurationTests
{
    [Fact]
    public void Reload_RootObservesLiveOverrideChange_AndReloadTokenFires()
    {
        var live = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:MinimumLevel:Override:Microsoft"] = "Warning",
                ["Serilog:WriteTo:0:Name"] = "Console",
            })
            .Build();

        var (root, _) = StepUpLoggingExtensions.SplitSerilogConfiguration(live);

        var token = root.GetReloadToken();
        Assert.Equal("Warning", root["Serilog:MinimumLevel:Override:Microsoft"]);

        live["Serilog:MinimumLevel:Override:Microsoft"] = "Debug";
        ((IConfigurationRoot)live).Reload();

        Assert.True(token.HasChanged);
        Assert.Equal("Debug", root["Serilog:MinimumLevel:Override:Microsoft"]);
    }

    [Fact]
    public void Root_CaseInsensitiveWriteToKey_IsHidden_GatedExposesIt()
    {
        var live = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["serilog:writeto:0:Name"] = "Console",
                ["Serilog:MinimumLevel:Default"] = "Information",
            })
            .Build();

        var (root, gated) = StepUpLoggingExtensions.SplitSerilogConfiguration(live);

        Assert.Null(root["Serilog:WriteTo:0:Name"]);
        Assert.Empty(root.GetSection("Serilog").GetSection("WriteTo").GetChildren());
        Assert.Equal("Console", gated["Serilog:WriteTo:0:Name"]);
    }

    [Fact]
    public void Root_WriteToFooNearMiss_IsNotFiltered()
    {
        var live = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:WriteToFoo"] = "kept",
                ["Serilog:WriteTo:0:Name"] = "Console",
            })
            .Build();

        var (root, gated) = StepUpLoggingExtensions.SplitSerilogConfiguration(live);

        Assert.Equal("kept", root["Serilog:WriteToFoo"]);
        Assert.Null(gated["Serilog:WriteToFoo"]);
    }

    [Fact]
    public void Using_AppearsInBothViews()
    {
        var live = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:Using:0"] = "Serilog.Sinks.Console",
                ["Serilog:WriteTo:0:Name"] = "Console",
            })
            .Build();

        var (root, gated) = StepUpLoggingExtensions.SplitSerilogConfiguration(live);

        Assert.Equal("Serilog.Sinks.Console", root["Serilog:Using:0"]);
        Assert.Equal("Serilog.Sinks.Console", gated["Serilog:Using:0"]);
    }
}
