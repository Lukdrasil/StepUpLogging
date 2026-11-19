using System;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Tests;

public class BypassLoggerDisposalTests
{
    private static StepUpLoggingOptions AutoOptions() => new()
    {
        Mode = StepUpMode.Auto,
        BaseLevel = "Warning",
        StepUpLevel = "Information",
        DurationSeconds = 300
    };

    private sealed class DisposeTrackingLogger : Serilog.ILogger, IDisposable
    {
        private int _disposeCount;
        public int DisposeCount => _disposeCount;

        public void Dispose() => Interlocked.Increment(ref _disposeCount);

        public void Write(LogEvent logEvent) { }
    }

    [Fact]
    public void Dispose_DisposesBypassLogger_Once()
    {
        var fake = new DisposeTrackingLogger();
        var controller = new StepUpLoggingController(AutoOptions(), fake);

        controller.Dispose();

        Assert.Equal(1, fake.DisposeCount);
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotDoubleDisposeBypassLogger()
    {
        var fake = new DisposeTrackingLogger();
        var controller = new StepUpLoggingController(AutoOptions(), fake);

        controller.Dispose();
        controller.Dispose();

        Assert.Equal(1, fake.DisposeCount);
    }

    [Fact]
    public void Dispose_DisposesBypassLoggerSetViaSetSummaryLogger()
    {
        var fake = new DisposeTrackingLogger();
        var controller = new StepUpLoggingController(AutoOptions(), null);
        controller.SetSummaryLogger(fake);

        controller.Dispose();

        Assert.Equal(1, fake.DisposeCount);
    }

    [Fact]
    public void LogFile_ReceivesBothTheGatedAndTheBypassLoggersEvents()
    {
        // Both output loggers write the same log file, so the file sink must be shared (ADR 0003): otherwise one of
        // the two writers cannot open it and its events are silently lost.
        var counts = TestHosts.RunInOneTrace(
            [],
            configure: null,
            services =>
            {
                var logger = services.GetRequiredService<Serilog.ILogger>();
                logger.Warning("{Token}", "SHARED_FILE_GATED");
                logger.ForContext(LogProperties.IsImmediate, true).Information("{Token}", "SHARED_FILE_BYPASS");
            },
            "SHARED_FILE_GATED", "SHARED_FILE_BYPASS");

        Assert.Equal(1, counts["SHARED_FILE_GATED"]);
        Assert.Equal(1, counts["SHARED_FILE_BYPASS"]);
    }
}
