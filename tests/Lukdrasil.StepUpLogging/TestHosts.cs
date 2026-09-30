using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Tests;

internal static class TestHosts
{
    public static HostApplicationBuilder NewBuilder()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SerilogStepUp:EnableOtlpExporter"] = "false",
        });
        return builder;
    }

    public static void StepUp(StepUpLoggingController controller)
    {
        controller.Trigger();
        Assert.True(SpinWait.SpinUntil(() => controller.LevelSwitch.MinimumLevel <= LogEventLevel.Information, 2000));
    }

    public static Dictionary<string, int> RunInOneTrace(
        Dictionary<string, string?> settings,
        Action<IServiceProvider, LoggerConfiguration>? configure,
        Action<IServiceProvider> scenario,
        params string[] tokens)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"stepup-host-{Guid.NewGuid():N}.log");
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
            if (configure is null)
            {
                builder.AddStepUpLogging(logFilePath: tempFile);
            }
            else
            {
                builder.AddStepUpLogging(configure, logFilePath: tempFile);
            }

            using var activity = new Activity("test-host-trace");
            activity.SetIdFormat(ActivityIdFormat.W3C);
            activity.Start();

            using (var host = builder.Build())
            {
                scenario(host.Services);
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
