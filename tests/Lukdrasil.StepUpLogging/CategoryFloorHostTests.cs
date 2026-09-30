using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Tests;

public class CategoryFloorHostTests
{
    private const string Floored = "Test.Floored";

    [Fact]
    public void Auto_SteppedUp_FlooredInformation_IsHeldBack_ThenExportedExactlyOnceByTheError()
    {
        RunInOneTrace([("CategoryFloors:" + Floored, "Warning")], (logger, controller, count) =>
        {
            StepUp(controller);
            logger.ForContext("SourceContext", Floored).Information("{Token}", "FLOOR_AUTO_INFO");
            Assert.Equal(0, count("FLOOR_AUTO_INFO"));

            logger.Error("{Token}", "FLOOR_AUTO_ERR");
            Assert.Equal(1, count("FLOOR_AUTO_INFO"));
        });
    }

    [Fact]
    public void Auto_SteppedUp_FloorBelowTheSwitch_ChangesNothing()
    {
        RunInOneTrace([("CategoryFloors:" + Floored, "Debug")], (logger, controller, count) =>
        {
            StepUp(controller);
            var floored = logger.ForContext("SourceContext", Floored);
            floored.Information("{Token}", "FLOOR_DEBUG_INFO");
            floored.Debug("{Token}", "FLOOR_DEBUG_DEBUG");

            Assert.Equal(1, count("FLOOR_DEBUG_INFO"));
            Assert.Equal(0, count("FLOOR_DEBUG_DEBUG"));
        });
    }

    [Fact]
    public void Auto_SteppedUp_MostSpecificFloorKeyDecides()
    {
        RunInOneTrace([("CategoryFloors:Test", "Warning"), ("CategoryFloors:" + Floored, "Information")], (logger, controller, count) =>
        {
            StepUp(controller);
            logger.ForContext("SourceContext", Floored + ".Child").Information("{Token}", "FLOOR_SPECIFIC_CHILD");
            logger.ForContext("SourceContext", "Test.Other").Information("{Token}", "FLOOR_SPECIFIC_OTHER");

            Assert.Equal(1, count("FLOOR_SPECIFIC_CHILD"));
            Assert.Equal(0, count("FLOOR_SPECIFIC_OTHER"));
        });
    }

    [Fact]
    public void AlwaysOn_FlooredInformation_IsHeldBack_ThenExportedExactlyOnceByTheError_OtherCategoryExports()
    {
        RunInOneTrace([("Mode", "AlwaysOn"), ("CategoryFloors:" + Floored, "Warning")], (logger, _, count) =>
        {
            logger.ForContext("SourceContext", Floored).Information("{Token}", "FLOOR_ALWAYSON_INFO");
            logger.ForContext("SourceContext", "Test.Other").Information("{Token}", "FLOOR_ALWAYSON_OTHER");
            Assert.Equal(0, count("FLOOR_ALWAYSON_INFO"));
            Assert.Equal(1, count("FLOOR_ALWAYSON_OTHER"));

            logger.Error("{Token}", "FLOOR_ALWAYSON_ERR");
            Assert.Equal(1, count("FLOOR_ALWAYSON_INFO"));
            Assert.Equal(1, count("FLOOR_ALWAYSON_OTHER"));
        });
    }

    [Fact]
    public void Disabled_FlooredInformation_IsHeldBack_ThenExportedExactlyOnceByTheError_OtherCategoryExports()
    {
        RunInOneTrace(
            [("Mode", "Disabled"), ("BaseLevel", "Information"), ("StepUpLevel", "Debug"), ("CategoryFloors:" + Floored, "Warning")],
            (logger, _, count) =>
            {
                logger.ForContext("SourceContext", Floored).Information("{Token}", "FLOOR_DISABLED_INFO");
                logger.ForContext("SourceContext", "Test.Other").Information("{Token}", "FLOOR_DISABLED_OTHER");
                Assert.Equal(0, count("FLOOR_DISABLED_INFO"));
                Assert.Equal(1, count("FLOOR_DISABLED_OTHER"));

                logger.Error("{Token}", "FLOOR_DISABLED_ERR");
                Assert.Equal(1, count("FLOOR_DISABLED_INFO"));
                Assert.Equal(1, count("FLOOR_DISABLED_OTHER"));
            });
    }

    [Fact]
    public void BlankExemptEntry_StartsAndTheFloorStillApplies()
    {
        RunInOneTrace(
            [("CategoryFloors:" + Floored, "Warning"), ("DiagnosticExemptCategories:0", " ")],
            (logger, controller, count) =>
            {
                StepUp(controller);
                logger.ForContext("SourceContext", Floored).Information("{Token}", "FLOOR_BLANK_INFO");
                logger.ForContext("SourceContext", "Test.Other").Information("{Token}", "FLOOR_BLANK_OTHER");

                Assert.Equal(0, count("FLOOR_BLANK_INFO"));
                Assert.Equal(1, count("FLOOR_BLANK_OTHER"));
            });
    }

    [Fact]
    public void FloorLevelIsParsedCaseInsensitively()
    {
        RunInOneTrace([("CategoryFloors:" + Floored, "warning")], (logger, controller, count) =>
        {
            StepUp(controller);
            logger.ForContext("SourceContext", Floored).Information("{Token}", "FLOOR_CASE_INFO");

            Assert.Equal(0, count("FLOOR_CASE_INFO"));
        });
    }

    private static void StepUp(StepUpLoggingController controller)
    {
        controller.Trigger();
        Assert.True(SpinWait.SpinUntil(() => controller.LevelSwitch.MinimumLevel <= LogEventLevel.Information, 2000));
    }

    private static void RunInOneTrace(
        (string Key, string Value)[] overrides,
        Action<Serilog.ILogger, StepUpLoggingController, Func<string, int>> scenario)
    {
        var captureKey = Guid.NewGuid().ToString("N");
        try
        {
            var settings = new Dictionary<string, string?>
            {
                ["SerilogStepUp:EnableOtlpExporter"] = "false",
                ["SerilogStepUp:EnablePreErrorBuffering"] = "true",
                ["SerilogStepUp:Mode"] = "Auto",
                ["SerilogStepUp:BaseLevel"] = "Warning",
                ["SerilogStepUp:StepUpLevel"] = "Information",
                ["SerilogStepUp:DurationSeconds"] = "300",
                ["Serilog:Using:0"] = typeof(KeyedCaptureSink).Assembly.GetName().Name,
                ["Serilog:WriteTo:0:Name"] = nameof(KeyedCaptureSink.StepUpCapture),
                ["Serilog:WriteTo:0:Args:key"] = captureKey,
            };
            foreach (var (key, value) in overrides)
            {
                settings["SerilogStepUp:" + key] = value;
            }

            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(settings);
            builder.AddStepUpLogging();

            using var activity = new Activity("category-floor");
            activity.SetIdFormat(ActivityIdFormat.W3C);
            activity.Start();

            using var host = builder.Build();
            scenario(
                host.Services.GetRequiredService<Serilog.ILogger>(),
                host.Services.GetRequiredService<StepUpLoggingController>(),
                token => KeyedCaptureSink.Count(captureKey, token));
        }
        finally
        {
            KeyedCaptureSink.Forget(captureKey);
        }
    }
}
