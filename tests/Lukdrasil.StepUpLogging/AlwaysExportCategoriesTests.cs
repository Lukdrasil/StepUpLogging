using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Tests;

public class AlwaysExportCategoriesTests
{
    private const string LifetimeCategory = "Microsoft.Hosting.Lifetime";

    [Fact]
    public void ListedInformationAtBaseLevelWarning_ThenErrorInSameTrace_IsExportedExactlyOnce()
    {
        var (counts, _) = Run(
            new() { ["SerilogStepUp:AlwaysExportCategories:0"] = LifetimeCategory },
            logger =>
            {
                logger.ForContext("SourceContext", LifetimeCategory).Information("{Token}", "ALWAYS_EXPORT_LIFETIME_INFO");
                logger.Error("{Token}", "ALWAYS_EXPORT_ERR");
            },
            "ALWAYS_EXPORT_LIFETIME_INFO");

        Assert.Equal(1, counts["ALWAYS_EXPORT_LIFETIME_INFO"]);
    }

    [Fact]
    public void ListedInformationAtBaseLevelWarning_WithoutError_IsExported()
    {
        var (counts, _) = Run(
            new() { ["SerilogStepUp:AlwaysExportCategories:0"] = LifetimeCategory },
            logger => logger.ForContext("SourceContext", LifetimeCategory).Information("{Token}", "ALWAYS_EXPORT_LIFETIME_ALONE"),
            "ALWAYS_EXPORT_LIFETIME_ALONE");

        Assert.Equal(1, counts["ALWAYS_EXPORT_LIFETIME_ALONE"]);
    }

    [Fact]
    public void NonListedInformationAtBaseLevelWarning_IsNotExportedOrMarked()
    {
        var (counts, collector) = Run(
            new() { ["SerilogStepUp:AlwaysExportCategories:0"] = LifetimeCategory },
            logger => logger.ForContext("SourceContext", "MyApp.Orders").Information("{Token}", "ALWAYS_EXPORT_OTHER_INFO"),
            "ALWAYS_EXPORT_OTHER_INFO");

        Assert.Equal(0, counts["ALWAYS_EXPORT_OTHER_INFO"]);
        Assert.DoesNotContain(LogProperties.IsImmediate, Assert.Single(collector.Events).Properties.Keys);
    }

    [Fact]
    public void ListedEvent_WithRedactLogEventProperties_ReachesCaptureSinkRedacted()
    {
        var (counts, collector) = Run(
            new()
            {
                ["SerilogStepUp:AlwaysExportCategories:0"] = LifetimeCategory,
                ["SerilogStepUp:RedactLogEventProperties"] = "true",
                ["SerilogStepUp:RedactionRegexes:0"] = "token=[^&]+",
            },
            logger => logger.ForContext("SourceContext", LifetimeCategory).Information("started with {Secret}", "token=super-secret"),
            "super-secret", "started with");

        var evt = Assert.Single(collector.Events);
        Assert.True(LogProperties.HasFlag(evt, LogProperties.IsImmediate));
        Assert.Equal("[REDACTED]", ((ScalarValue)evt.Properties["Secret"]).Value);
        Assert.Equal(1, counts["started with"]);
        Assert.Equal(0, counts["super-secret"]);
    }

    [Fact]
    public void EmptyList_RegistersNoEnricher()
    {
        var (counts, collector) = Run(
            [],
            logger => logger.ForContext("SourceContext", LifetimeCategory).Information("{Token}", "ALWAYS_EXPORT_EMPTY_LIST"),
            "ALWAYS_EXPORT_EMPTY_LIST");

        Assert.Equal(0, counts["ALWAYS_EXPORT_EMPTY_LIST"]);
        Assert.DoesNotContain(LogProperties.IsImmediate, Assert.Single(collector.Events).Properties.Keys);
    }

    [Fact]
    public void BlankEntries_AreFiltered_AndMatchNoDotRootedCategory()
    {
        var (counts, collector) = Run(
            new()
            {
                ["SerilogStepUp:AlwaysExportCategories:0"] = "",
                ["SerilogStepUp:AlwaysExportCategories:1"] = "   ",
            },
            logger => logger.ForContext("SourceContext", ".Dotted").Information("{Token}", "ALWAYS_EXPORT_BLANK"),
            "ALWAYS_EXPORT_BLANK");

        Assert.Equal(0, counts["ALWAYS_EXPORT_BLANK"]);
        Assert.DoesNotContain(LogProperties.IsImmediate, Assert.Single(collector.Events).Properties.Keys);
    }

    private static (Dictionary<string, int> Counts, CollectingSink Collector) Run(
        Dictionary<string, string?> settings,
        Action<Serilog.ILogger> scenario,
        params string[] tokens)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"stepup-always-export-{Guid.NewGuid():N}.log");
        var directory = Path.GetDirectoryName(tempFile)!;
        var pattern = Path.GetFileNameWithoutExtension(tempFile) + "*";
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SerilogStepUp:EnableOtlpExporter"] = "false",
                ["SerilogStepUp:EnablePreErrorBuffering"] = "true",
                ["SerilogStepUp:Mode"] = "Auto",
                ["SerilogStepUp:BaseLevel"] = "Warning",
                ["SerilogStepUp:StepUpLevel"] = "Information",
                ["SerilogStepUp:DurationSeconds"] = "300",
            });
            builder.Configuration.AddInMemoryCollection(settings);
            var collector = new CollectingSink();
            builder.AddStepUpLogging(
                (_, lc) => lc.WriteTo.Sink(collector),
                logFilePath: tempFile);

            using var activity = new Activity("always-export");
            activity.SetIdFormat(ActivityIdFormat.W3C);
            activity.Start();

            using (var host = builder.Build())
            {
                scenario(host.Services.GetRequiredService<Serilog.ILogger>());
            }

            var lines = Directory.GetFiles(directory, pattern).SelectMany(File.ReadAllLines).ToArray();
            return (tokens.ToDictionary(t => t, t => lines.Count(l => l.Contains(t, StringComparison.Ordinal))), collector);
        }
        finally
        {
            foreach (var f in Directory.GetFiles(directory, pattern))
            {
                try { File.Delete(f); } catch { }
            }
        }
    }
}
