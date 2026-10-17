using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Issue #79: <see cref="StepUpLoggingOptions.PreErrorBufferExcludeCategories"/> keeps a category's events below
/// Warning out of the pre-error buffer, so an Error flush does not export them.
/// </summary>
public class PreErrorBufferExcludeCategoriesTests
{
    private const string Routing = "Microsoft.AspNetCore.Routing";
    private const string AppCategory = "MyApp.Orders";
    private const string ExcludeKey = "SerilogStepUp:PreErrorBufferExcludeCategories:0";

    [Fact]
    public void ExcludedInformation_ThenErrorInSameTrace_IsNotExported()
    {
        var counts = Run(
            new() { [ExcludeKey] = Routing },
            logger =>
            {
                logger.ForContext("SourceContext", Routing).Information("{Token}", "EXCLUDE_ROUTING_INFO_79");
                logger.Error("{Token}", "EXCLUDE_ERR_79");
            },
            "EXCLUDE_ROUTING_INFO_79", "EXCLUDE_ERR_79");

        Assert.Equal(0, counts["EXCLUDE_ROUTING_INFO_79"]);
        Assert.Equal(1, counts["EXCLUDE_ERR_79"]);
    }

    [Fact]
    public void ExcludedChildCategoryInformation_ThenErrorInSameTrace_IsNotExported()
    {
        var counts = Run(
            new() { [ExcludeKey] = Routing },
            logger =>
            {
                logger.ForContext("SourceContext", Routing + ".EndpointMiddleware").Information("{Token}", "EXCLUDE_ROUTING_CHILD_INFO_79");
                logger.Error("{Token}", "EXCLUDE_ERR_79");
            },
            "EXCLUDE_ROUTING_CHILD_INFO_79");

        Assert.Equal(0, counts["EXCLUDE_ROUTING_CHILD_INFO_79"]);
    }

    [Fact]
    public void ApplicationInformation_ThenErrorInSameTrace_IsStillFlushed()
    {
        var counts = Run(
            new() { [ExcludeKey] = Routing },
            logger =>
            {
                logger.ForContext("SourceContext", Routing).Information("{Token}", "EXCLUDE_ROUTING_INFO_79");
                logger.ForContext("SourceContext", AppCategory).Information("{Token}", "EXCLUDE_APP_INFO_79");
                logger.Error("{Token}", "EXCLUDE_ERR_79");
            },
            "EXCLUDE_APP_INFO_79");

        Assert.Equal(1, counts["EXCLUDE_APP_INFO_79"]);
    }

    [Fact]
    public void ExcludedWarning_HeldAtBaseLevelError_IsStillFlushedByTheError()
    {
        var counts = Run(
            new() { [ExcludeKey] = Routing, ["SerilogStepUp:BaseLevel"] = "Error" },
            logger =>
            {
                logger.ForContext("SourceContext", Routing).Warning("{Token}", "EXCLUDE_ROUTING_WARN_79");
                logger.Error("{Token}", "EXCLUDE_ERR_79");
            },
            "EXCLUDE_ROUTING_WARN_79");

        Assert.Equal(1, counts["EXCLUDE_ROUTING_WARN_79"]);
    }

    [Fact]
    public void Diagnostic_ExcludedDebug_IsExported()
    {
        var counts = Run(
            new() { [ExcludeKey] = Routing, ["SerilogStepUp:Mode"] = "Diagnostic" },
            logger => logger.ForContext("SourceContext", Routing).Debug("{Token}", "EXCLUDE_ROUTING_DIAG_DEBUG_79"),
            "EXCLUDE_ROUTING_DIAG_DEBUG_79");

        Assert.Equal(1, counts["EXCLUDE_ROUTING_DIAG_DEBUG_79"]);
    }

    [Fact]
    public void NoExcludedCategories_FrameworkInformation_ThenErrorInSameTrace_IsFlushed()
    {
        var counts = Run(
            new() { ["SerilogStepUp:FrameworkCategoryDefaults"] = "None" },
            logger =>
            {
                logger.ForContext("SourceContext", Routing).Information("{Token}", "EXCLUDE_DEFAULT_ROUTING_INFO_79");
                logger.Error("{Token}", "EXCLUDE_ERR_79");
            },
            "EXCLUDE_DEFAULT_ROUTING_INFO_79");

        Assert.Equal(1, counts["EXCLUDE_DEFAULT_ROUTING_INFO_79"]);
    }

    private static Dictionary<string, int> Run(
        Dictionary<string, string?> settings,
        Action<Serilog.ILogger> scenario,
        params string[] tokens)
        => TestHosts.RunInOneTrace(
            settings,
            configure: null,
            services => scenario(services.GetRequiredService<Serilog.ILogger>()),
            tokens);
}
