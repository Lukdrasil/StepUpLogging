using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Xunit;
using static Lukdrasil.StepUpLogging.Tests.TestHosts;

namespace Lukdrasil.StepUpLogging.Tests;

public class V2ApiCleanupTests
{
    [Fact]
    public void ConsoleLogging_DrivenByOptions_WiresWithoutError()
    {
        var builder = NewBuilder();
        builder.AddStepUpLogging(opts =>
        {
            opts.EnableOtlpExporter = false;
            opts.EnableConsoleLogging = true;
        });

        using var host = builder.Build();

        var logger = host.Services.GetRequiredService<Serilog.ILogger>();
        Assert.NotNull(logger);
        logger.Warning("console-via-options smoke");
    }

    [Fact]
    public void ConfigureDelegate_InvokedWithServiceProviderAndLoggerConfiguration()
    {
        var builder = NewBuilder();

        IServiceProvider? capturedProvider = null;
        LoggerConfiguration? capturedConfig = null;

        builder.AddStepUpLogging((IServiceProvider sp, LoggerConfiguration lc) =>
        {
            capturedProvider = sp;
            capturedConfig = lc;
        });

        using var host = builder.Build();

        // Force the Serilog factory (and thus the configure callback) to run.
        var logger = host.Services.GetRequiredService<Serilog.ILogger>();
        Assert.NotNull(logger);

        Assert.NotNull(capturedProvider);
        Assert.NotNull(capturedConfig);
        // The provider must be usable — resolving a known registered service must not throw.
        Assert.NotNull(capturedProvider!.GetRequiredService<StepUpLoggingController>());
    }
}
