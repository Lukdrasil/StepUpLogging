using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Characterization pin for ADR 0014 (F11): the <see cref="PreErrorBufferSink"/> shutdown flush
/// writes into the bypass logger, whose lifetime is owned by <see cref="StepUpLoggingController"/>.
/// Both are disposed by the DI container in reverse registration order. If the controller were
/// disposed first, the flush would write into a disposed <c>Serilog.Core.Logger</c> — a silent
/// no-op — and the final buffered events would vanish. This test builds the real container and
/// asserts they survive <see cref="ServiceProvider"/> disposal.
/// </summary>
public class ShutdownFlushOrderingTests
{
    [Fact]
    public void BufferedEvents_SurviveServiceProviderDisposal_ReachBypassLogger()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"stepup-b07-{Guid.NewGuid():N}.log");
        const string bufferedToken = "BUFFERED_SURVIVES_SHUTDOWN_b07";

        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SerilogStepUp:EnableOtlpExporter"] = "false",
                ["SerilogStepUp:EnablePreErrorBuffering"] = "true",
                ["SerilogStepUp:BaseLevel"] = "Warning",
                ["SerilogStepUp:StepUpLevel"] = "Information",
                ["SerilogStepUp:DurationSeconds"] = "300",
            });
            builder.AddStepUpLogging(logFilePath: tempFile);

            using var activity = new Activity("b07-shutdown-flush");
            activity.SetIdFormat(ActivityIdFormat.W3C);
            activity.Start();

            using (var host = builder.Build())
            {
                var logger = host.Services.GetRequiredService<Serilog.ILogger>();

                // Sub-BaseLevel events: gated out of the step-up sink, held only in the pre-error
                // buffer. No Error is emitted, so nothing flushes during the run.
                logger.Information("{Token} #1", bufferedToken);
                logger.Information("{Token} #2", bufferedToken);
                logger.Debug("{Token} #3", bufferedToken);
            }

            var contents = string.Concat(Array.ConvertAll(
                Directory.GetFiles(
                    Path.GetDirectoryName(tempFile)!,
                    Path.GetFileNameWithoutExtension(tempFile) + "*"),
                File.ReadAllText));
            Assert.Contains(bufferedToken, contents);
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
