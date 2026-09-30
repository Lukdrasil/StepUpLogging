using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Issue #29: an event is exported at most once. StepUpSink makes the export decision once per
/// event and hands only the held-back ones to the pre-error buffer, so an Error flush never
/// re-exports what the gated output already carried.
/// </summary>
public class PreErrorBufferExactlyOnceTests
{
    private const string EfCategory = "Microsoft.EntityFrameworkCore.Database.Command";

    [Fact]
    public void WarningAtBaseLevel_ThenErrorInSameTrace_IsExportedExactlyOnce()
    {
        var counts = RunInOneTrace((logger, _) =>
        {
            logger.Warning("{Token}", "EXACTLY_ONCE_WARN_29");
            logger.Error("{Token}", "EXACTLY_ONCE_ERR_29");
        }, "EXACTLY_ONCE_WARN_29", "EXACTLY_ONCE_ERR_29");

        Assert.Equal(1, counts["EXACTLY_ONCE_WARN_29"]);
        Assert.Equal(1, counts["EXACTLY_ONCE_ERR_29"]);
    }

    [Fact]
    public void InformationWhileSteppedUp_ThenErrorInSameTrace_IsExportedExactlyOnce()
    {
        var counts = RunInOneTrace((logger, controller) =>
        {
            StepUp(controller);
            logger.Information("{Token}", "EXACTLY_ONCE_STEPPED_INFO_29");
            logger.Error("{Token}", "EXACTLY_ONCE_ERR_29");
        }, "EXACTLY_ONCE_STEPPED_INFO_29");

        Assert.Equal(1, counts["EXACTLY_ONCE_STEPPED_INFO_29"]);
    }

    [Fact]
    public void InformationBelowBaseLevel_ThenErrorInSameTrace_IsExportedExactlyOnceByTheFlush()
    {
        var counts = RunInOneTrace((logger, _) =>
        {
            logger.Information("{Token}", "EXACTLY_ONCE_HELD_INFO_29");
            logger.Error("{Token}", "EXACTLY_ONCE_ERR_29");
        }, "EXACTLY_ONCE_HELD_INFO_29");

        Assert.Equal(1, counts["EXACTLY_ONCE_HELD_INFO_29"]);
    }

    [Fact]
    public void NeverStepUpInformationBelowBaseLevel_ThenError_IsExportedExactlyOnceByTheFlush()
    {
        var counts = RunInOneTrace((logger, _) =>
        {
            logger.ForContext("SourceContext", EfCategory).Information("{Token}", "EXACTLY_ONCE_EF_INFO_29");
            logger.Error("{Token}", "EXACTLY_ONCE_ERR_29");
        }, "EXACTLY_ONCE_EF_INFO_29");

        Assert.Equal(1, counts["EXACTLY_ONCE_EF_INFO_29"]);
    }

    [Fact]
    public void NeverStepUpInformationWhileSteppedUp_IsHeldBack_ThenExportedExactlyOnceByTheFlush()
    {
        var counts = RunInOneTrace((logger, controller) =>
        {
            StepUp(controller);
            logger.ForContext("SourceContext", EfCategory).Information("{Token}", "EXACTLY_ONCE_EF_STEPPED_INFO_29");
            logger.Error("{Token}", "EXACTLY_ONCE_ERR_29");
        }, "EXACTLY_ONCE_EF_STEPPED_INFO_29");

        Assert.Equal(1, counts["EXACTLY_ONCE_EF_STEPPED_INFO_29"]);
    }

    private static void StepUp(StepUpLoggingController controller)
    {
        controller.Trigger();
        Assert.True(SpinWait.SpinUntil(() => controller.LevelSwitch.MinimumLevel <= LogEventLevel.Information, 2000));
    }

    private static Dictionary<string, int> RunInOneTrace(
        Action<Serilog.ILogger, StepUpLoggingController> scenario,
        params string[] tokens)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"stepup-exactly-once-{Guid.NewGuid():N}.log");
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
            builder.AddStepUpLogging(logFilePath: tempFile);

            using var activity = new Activity("exactly-once-29");
            activity.SetIdFormat(ActivityIdFormat.W3C);
            activity.Start();

            using (var host = builder.Build())
            {
                scenario(
                    host.Services.GetRequiredService<Serilog.ILogger>(),
                    host.Services.GetRequiredService<StepUpLoggingController>());
            }

            var lines = Directory.GetFiles(directory, pattern).SelectMany(File.ReadAllLines).ToArray();
            return tokens.ToDictionary(t => t, t => lines.Count(l => l.Contains(t, StringComparison.Ordinal)));
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
