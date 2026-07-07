using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog.Events;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

public class ConfigWriteToGatingTests
{
    [Fact]
    public void SplitSerilogConfiguration_PartitionsWriteToFromEverythingElse()
    {
        var appConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:MinimumLevel:Default"] = "Information",
                ["Serilog:WriteTo:0:Name"] = "Console",
                ["Serilog:Enrich:0"] = "FromLogContext",
                ["Serilog:Using:0"] = "Serilog.Sinks.Console",
            })
            .Build();

        var (root, gated) = StepUpLoggingExtensions.SplitSerilogConfiguration(appConfig);

        // Root keeps MinimumLevel + Enrich, drops WriteTo entirely.
        Assert.Equal("Information", root["Serilog:MinimumLevel:Default"]);
        Assert.Equal("FromLogContext", root["Serilog:Enrich:0"]);
        Assert.DoesNotContain(root.AsEnumerable(), kv =>
            kv.Key.StartsWith("Serilog:WriteTo", System.StringComparison.OrdinalIgnoreCase)
            && kv.Value is not null);

        // Gated keeps WriteTo + Using only, drops MinimumLevel + Enrich.
        Assert.Equal("Console", gated["Serilog:WriteTo:0:Name"]);
        Assert.Equal("Serilog.Sinks.Console", gated["Serilog:Using:0"]);
        Assert.Null(gated["Serilog:MinimumLevel:Default"]);
        Assert.DoesNotContain(gated.AsEnumerable(), kv =>
            kv.Key.StartsWith("Serilog:Enrich", System.StringComparison.OrdinalIgnoreCase)
            && kv.Value is not null);
    }

    [Fact]
    public void ConfigWriteToSink_HonorsLevelSwitch_GatedThenSteppedUp()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"stepup-b03-{System.Guid.NewGuid():N}.log");

        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SerilogStepUp:EnableOtlpExporter"] = "false",
                ["SerilogStepUp:EnablePreErrorBuffering"] = "false",
                ["SerilogStepUp:BaseLevel"] = "Warning",
                ["SerilogStepUp:StepUpLevel"] = "Information",
                ["Serilog:Using:0"] = "Serilog.Sinks.File",
                ["Serilog:WriteTo:0:Name"] = "File",
                ["Serilog:WriteTo:0:Args:path"] = tempFile,
            });
            builder.AddStepUpLogging();

            const string beforeToken = "BEFORE_STEPUP_TOKEN_b03";
            const string afterToken = "AFTER_STEPUP_TOKEN_b03";

            using (var host = builder.Build())
            {
                var logger = host.Services.GetRequiredService<Serilog.ILogger>();
                var controller = host.Services.GetRequiredService<StepUpLoggingController>();

                // Not stepped up (BaseLevel=Warning): Information must be gated out of the config File sink.
                logger.Information(beforeToken);

                controller.Trigger();
                SpinWait.SpinUntil(() => controller.LevelSwitch.MinimumLevel <= LogEventLevel.Information, 2000);

                // Stepped up: Information now passes the LevelSwitch and reaches the config File sink.
                logger.Information(afterToken);
            }

            var contents = File.ReadAllText(tempFile);
            Assert.DoesNotContain(beforeToken, contents);
            Assert.Contains(afterToken, contents);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
