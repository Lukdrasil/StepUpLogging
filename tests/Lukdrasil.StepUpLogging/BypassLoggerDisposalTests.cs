using System;
using System.IO;
using System.Threading;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Parsing;

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
    public void TwoLoggers_SharingSameFilePath_DoNotThrowAndFileHasContent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stepup-shared-{Guid.NewGuid():N}.log");
        try
        {
            var inner = new LoggerConfiguration()
                .WriteTo.Async(a => a.File(
                    formatter: new CompactJsonFormatter(),
                    path: path,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    shared: true))
                .CreateLogger();

            var bypass = new LoggerConfiguration()
                .WriteTo.Async(a => a.File(
                    formatter: new CompactJsonFormatter(),
                    path: path,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    shared: true))
                .CreateLogger();

            var mt = new MessageTemplateParser().Parse("event {Source}");
            inner.Write(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, mt,
                new[] { new LogEventProperty("Source", new ScalarValue("inner")) }));
            bypass.Write(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, mt,
                new[] { new LogEventProperty("Source", new ScalarValue("bypass")) }));

            (inner as IDisposable)?.Dispose();
            (bypass as IDisposable)?.Dispose();

            var rolledPath = Directory.GetFiles(
                Path.GetDirectoryName(path)!,
                Path.GetFileNameWithoutExtension(path) + "*");
            Assert.NotEmpty(rolledPath);
            var total = 0L;
            foreach (var f in rolledPath)
            {
                total += new FileInfo(f).Length;
            }
            Assert.True(total > 0, "shared file should contain written events");
        }
        finally
        {
            foreach (var f in Directory.GetFiles(
                Path.GetDirectoryName(path)!,
                Path.GetFileNameWithoutExtension(path) + "*"))
            {
                try { File.Delete(f); } catch { }
            }
        }
    }
}
