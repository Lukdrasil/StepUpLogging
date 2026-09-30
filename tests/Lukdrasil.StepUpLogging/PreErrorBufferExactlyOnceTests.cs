using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using static Lukdrasil.StepUpLogging.Tests.TestHosts;

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

    private static Dictionary<string, int> RunInOneTrace(
        Action<Serilog.ILogger, StepUpLoggingController> scenario,
        params string[] tokens) =>
        TestHosts.RunInOneTrace(
            [],
            null,
            services => scenario(
                services.GetRequiredService<Serilog.ILogger>(),
                services.GetRequiredService<StepUpLoggingController>()),
            tokens);
}
