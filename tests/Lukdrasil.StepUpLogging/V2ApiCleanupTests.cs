using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;
using static Lukdrasil.StepUpLogging.Tests.TestHosts;

namespace Lukdrasil.StepUpLogging.Tests;

public class V2ApiCleanupTests
{
    [Fact]
    public void ConfigureDelegate_InvokedWithServiceProviderAndLoggerConfiguration()
    {
        var builder = NewBuilder();
        var capture = new CaptureSink();
        IServiceProvider? capturedProvider = null;

        builder.AddStepUpLogging((IServiceProvider sp, LoggerConfiguration lc) =>
        {
            capturedProvider = sp;
            lc.WriteTo.Sink(capture);
        });

        using (var host = builder.Build())
        {
            host.Services.GetRequiredService<Serilog.ILogger>().Warning("configure-delegate probe");

            Assert.Same(host.Services.GetRequiredService<StepUpLoggingController>(), capturedProvider!.GetRequiredService<StepUpLoggingController>());
        }

        Assert.Contains(capture.Events, e => e.MessageTemplate.Text == "configure-delegate probe");
    }

    private sealed class CaptureSink : ILogEventSink
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<LogEvent> _events = new();

        public LogEvent[] Events => [.. _events];

        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
    }
}
