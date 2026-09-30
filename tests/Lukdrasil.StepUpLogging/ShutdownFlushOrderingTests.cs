using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// ADR 0023 (supersedes ADR 0014): disposing the host drops held-back events without export. An
/// event the gated output already exported is not exported a second time by the buffer.
/// </summary>
public class ShutdownFlushOrderingTests
{
    [Fact]
    public void HeldBackEvents_AreDroppedOnServiceProviderDisposal()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"stepup-b07-{Guid.NewGuid():N}.log");
        const string exportedToken = "EXPORTED_WARNING_ONCE_b07";
        const string heldBackToken = "HELD_BACK_DROPPED_b07";

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

            using var activity = new Activity("b07-shutdown-drop");
            activity.SetIdFormat(ActivityIdFormat.W3C);
            activity.Start();

            using (var host = builder.Build())
            {
                var logger = host.Services.GetRequiredService<Serilog.ILogger>();

                logger.Warning("{Token}", exportedToken);
                logger.Information("{Token}", heldBackToken);
            }

            var lines = Directory.GetFiles(
                    Path.GetDirectoryName(tempFile)!,
                    Path.GetFileNameWithoutExtension(tempFile) + "*")
                .SelectMany(File.ReadAllLines)
                .ToArray();
            Assert.Equal(1, lines.Count(l => l.Contains(exportedToken, StringComparison.Ordinal)));
            Assert.Equal(0, lines.Count(l => l.Contains(heldBackToken, StringComparison.Ordinal)));
        }
        finally
        {
            foreach (var f in Directory.GetFiles(
                Path.GetDirectoryName(tempFile)!,
                Path.GetFileNameWithoutExtension(tempFile) + "*"))
            {
                try { File.Delete(f); } catch { }
            }
        }
    }

    [Fact]
    public void EmitRequestSummary_AfterControllerDisposed_DoesNotThrow()
    {
        var opts = new StepUpLoggingOptions
        {
            Mode = StepUpMode.Auto,
            BaseLevel = "Warning",
            StepUpLevel = "Information",
            DurationSeconds = 300,
        };
        var controller = new StepUpLoggingController(opts, null);

        controller.Dispose();

        var ex = Record.Exception(() =>
            controller.EmitRequestSummary("GET", "/health", 200, 1.2));
        Assert.Null(ex);
    }
}
