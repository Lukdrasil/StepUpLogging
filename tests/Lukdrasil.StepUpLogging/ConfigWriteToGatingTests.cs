using System;
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
    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    [Fact]
    public void BypassLogger_RoutesImmediateSummaryAndPreErrorFlush_ToConfigWriteToSink_WithoutDoubleEmit()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"stepup-b01-bypass-{Guid.NewGuid():N}.log");

        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SerilogStepUp:EnableOtlpExporter"] = "false",
                ["SerilogStepUp:EnablePreErrorBuffering"] = "true",
                ["SerilogStepUp:BaseLevel"] = "Warning",
                ["SerilogStepUp:StepUpLevel"] = "Information",
                ["Serilog:Using:0"] = "Serilog.Sinks.File",
                ["Serilog:WriteTo:0:Name"] = "File",
                ["Serilog:WriteTo:0:Args:path"] = tempFile,
                ["Serilog:WriteTo:0:Args:shared"] = "true",
            });
            builder.AddStepUpLogging();

            var immToken = "IMMEDIATE_" + Guid.NewGuid().ToString("N");
            var summaryToken = "SUMMARY_" + Guid.NewGuid().ToString("N");
            var bufferedToken = "BUFFERED_" + Guid.NewGuid().ToString("N");

            using (var host = builder.Build())
            {
                var logger = host.Services.GetRequiredService<Serilog.ILogger>();

                logger.ForContext(LogProperties.IsImmediate, true).Information(immToken);
                logger.ForContext(LogProperties.IsRequestSummary, true).Information(summaryToken);

                // Below the Warning base, so the gated sink drops it; only the pre-error flush (on the
                // Error below, same global context) can carry it to the bypass logger's config sink.
                logger.Information(bufferedToken);
                logger.Error("error-that-flushes-the-buffer");
            }

            var contents = File.ReadAllText(tempFile);

            // Immediate + summary reach the config sink exactly once (the gated sink drops them).
            Assert.Equal(1, CountOccurrences(contents, immToken));
            Assert.Equal(1, CountOccurrences(contents, summaryToken));
            // Pre-error flush reaches the config sink too.
            Assert.Contains(bufferedToken, contents);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void AutoMode_WithBaseLevelEqualToStepUpLevel_LogsLevelOrderWarningAtStartup()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"stepup-b01-warn-{Guid.NewGuid():N}.log");

        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SerilogStepUp:EnableOtlpExporter"] = "false",
                ["SerilogStepUp:Mode"] = "Auto",
                ["SerilogStepUp:BaseLevel"] = "Information",
                ["SerilogStepUp:StepUpLevel"] = "Information",
                ["Serilog:Using:0"] = "Serilog.Sinks.File",
                ["Serilog:WriteTo:0:Name"] = "File",
                ["Serilog:WriteTo:0:Args:path"] = tempFile,
                ["Serilog:WriteTo:0:Args:shared"] = "true",
            });
            builder.AddStepUpLogging();

            using (var host = builder.Build())
            {
                // Resolving the logger forces the AddSerilog callback where the warning is emitted.
                _ = host.Services.GetRequiredService<Serilog.ILogger>();
            }

            var contents = File.ReadAllText(tempFile);
            Assert.Contains("not more verbose", contents);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData("AlwaysOn")]
    [InlineData("Disabled")]
    public void NonAutoMode_WithEqualLevels_StartsWithoutLevelOrderWarning(string mode)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"stepup-b01-nowarn-{Guid.NewGuid():N}.log");

        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SerilogStepUp:EnableOtlpExporter"] = "false",
                ["SerilogStepUp:Mode"] = mode,
                ["SerilogStepUp:BaseLevel"] = "Information",
                ["SerilogStepUp:StepUpLevel"] = "Information",
                ["Serilog:Using:0"] = "Serilog.Sinks.File",
                ["Serilog:WriteTo:0:Name"] = "File",
                ["Serilog:WriteTo:0:Args:path"] = tempFile,
                ["Serilog:WriteTo:0:Args:shared"] = "true",
            });
            builder.AddStepUpLogging();

            using (var host = builder.Build())
            {
                _ = host.Services.GetRequiredService<Serilog.ILogger>();
            }

            var contents = File.Exists(tempFile) ? File.ReadAllText(tempFile) : string.Empty;
            Assert.DoesNotContain("not more verbose", contents);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

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
    public void SplitSerilogConfiguration_AncestorSectionsResolve_ThroughGetSectionApi()
    {
        var appConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:MinimumLevel:Default"] = "Information",
                ["Serilog:WriteTo:0:Name"] = "Console",
            })
            .Build();

        var (root, gated) = StepUpLoggingExtensions.SplitSerilogConfiguration(appConfig);

        // Root: WriteTo is entirely absent when walked through GetSection/GetChildren, while the
        // MinimumLevel ancestor chain still resolves via the section API (not just the indexer).
        Assert.Empty(root.GetSection("Serilog").GetSection("WriteTo").GetChildren());
        Assert.Equal("Information", root.GetSection("Serilog").GetSection("MinimumLevel")["Default"]);

        // Gated: WriteTo is present through GetSection/GetChildren, while MinimumLevel is empty.
        Assert.NotEmpty(gated.GetSection("Serilog").GetSection("WriteTo").GetChildren());
        Assert.Empty(gated.GetSection("Serilog").GetSection("MinimumLevel").GetChildren());
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
                // shared:true is required once a config File sink attaches to both the gated and the
                // bypass logger, or the two writers contend for the file lock (ADR 0003).
                ["Serilog:WriteTo:0:Args:shared"] = "true",
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
