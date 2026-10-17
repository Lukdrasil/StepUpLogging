using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// ADR 0029: <see cref="StepUpLoggingOptions.FrameworkCategoryDefaults"/> floors framework categories at Warning and
/// keeps them out of the pre-error buffer, while application categories export and flush as before.
/// </summary>
public class FrameworkCategoryDefaultsTests
{
    private const string Hosting = "Microsoft.AspNetCore.Hosting.Diagnostics";
    private const string Routing = "Microsoft.AspNetCore.Routing.EndpointMiddleware";
    private const string HttpResults = "Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult";
    private const string HttpClientHandler = "System.Net.Http.HttpClient.Keycloak.ClientHandler";
    private const string Npgsql = "Npgsql.Command";
    private const string Cors = "Microsoft.AspNetCore.Cors.Infrastructure.CorsService";
    private const string AppCategory = "MyApp.Tokens";
    private const string PresetKey = "SerilogStepUp:FrameworkCategoryDefaults";

    [Fact]
    public void FrameworkInformation_ThenErrorInSameTrace_IsNotExported()
    {
        var counts = Run(
            [],
            logger =>
            {
                logger.ForContext("SourceContext", Hosting).Information("{Token}", "FCD_HOSTING_INFO");
                logger.ForContext("SourceContext", Routing).Information("{Token}", "FCD_ROUTING_INFO");
                logger.ForContext("SourceContext", HttpClientHandler).Information("{Token}", "FCD_HTTPCLIENT_INFO");
                logger.ForContext("SourceContext", Npgsql).Information("{Token}", "FCD_NPGSQL_INFO");
                logger.ForContext("SourceContext", HttpResults).Information("{Token}", "FCD_RESULTS_INFO");
                logger.Error("{Token}", "FCD_ERR");
            },
            "FCD_HOSTING_INFO", "FCD_ROUTING_INFO", "FCD_HTTPCLIENT_INFO", "FCD_NPGSQL_INFO", "FCD_RESULTS_INFO", "FCD_ERR");

        Assert.Equal(0, counts["FCD_HOSTING_INFO"]);
        Assert.Equal(0, counts["FCD_ROUTING_INFO"]);
        Assert.Equal(0, counts["FCD_HTTPCLIENT_INFO"]);
        Assert.Equal(0, counts["FCD_NPGSQL_INFO"]);
        Assert.Equal(0, counts["FCD_RESULTS_INFO"]);
        Assert.Equal(1, counts["FCD_ERR"]);
    }

    [Fact]
    public void FrameworkInformation_AtSwitchInformation_IsNotExported()
    {
        var counts = Run(
            new() { ["SerilogStepUp:BaseLevel"] = "Information" },
            logger => logger.ForContext("SourceContext", Hosting).Information("{Token}", "FCD_HOSTING_LIVE_INFO"),
            "FCD_HOSTING_LIVE_INFO");

        Assert.Equal(0, counts["FCD_HOSTING_LIVE_INFO"]);
    }

    [Fact]
    public void ApplicationInformation_ThenErrorInSameTrace_IsStillFlushed()
    {
        var counts = Run(
            [],
            logger =>
            {
                logger.ForContext("SourceContext", Hosting).Information("{Token}", "FCD_HOSTING_INFO");
                logger.ForContext("SourceContext", AppCategory).Information("{Token}", "FCD_APP_INFO");
                logger.Error("{Token}", "FCD_ERR");
            },
            "FCD_APP_INFO");

        Assert.Equal(1, counts["FCD_APP_INFO"]);
    }

    [Fact]
    public void FrameworkWarning_IsExported()
    {
        var counts = Run(
            [],
            logger => logger.ForContext("SourceContext", HttpClientHandler).Warning("{Token}", "FCD_HTTPCLIENT_WARN"),
            "FCD_HTTPCLIENT_WARN");

        Assert.Equal(1, counts["FCD_HTTPCLIENT_WARN"]);
    }

    [Fact]
    public void KeptInBufferCategoryInformation_ThenErrorInSameTrace_IsFlushed()
    {
        var counts = Run(
            [],
            logger =>
            {
                logger.ForContext("SourceContext", Cors).Information("{Token}", "FCD_CORS_INFO");
                logger.Error("{Token}", "FCD_ERR");
            },
            "FCD_CORS_INFO");

        Assert.Equal(1, counts["FCD_CORS_INFO"]);
    }

    [Fact]
    public void KeptInBufferCategoryInformation_AtSwitchInformation_IsNotExported()
    {
        var counts = Run(
            new() { ["SerilogStepUp:BaseLevel"] = "Information" },
            logger => logger.ForContext("SourceContext", Cors).Information("{Token}", "FCD_CORS_LIVE_INFO"),
            "FCD_CORS_LIVE_INFO");

        Assert.Equal(0, counts["FCD_CORS_LIVE_INFO"]);
    }

    [Fact]
    public void PresetNone_FrameworkInformation_ThenErrorInSameTrace_IsFlushed()
    {
        var counts = Run(
            new() { [PresetKey] = "None" },
            logger =>
            {
                logger.ForContext("SourceContext", Hosting).Information("{Token}", "FCD_NONE_HOSTING_INFO");
                logger.Error("{Token}", "FCD_ERR");
            },
            "FCD_NONE_HOSTING_INFO");

        Assert.Equal(1, counts["FCD_NONE_HOSTING_INFO"]);
    }

    [Fact]
    public void UserFloor_ShorterThanTheDefaultKey_OverridesIt()
    {
        var counts = Run(
            new()
            {
                ["SerilogStepUp:BaseLevel"] = "Information",
                ["SerilogStepUp:CategoryFloors:System.Net.Http"] = "Information",
            },
            logger => logger.ForContext("SourceContext", HttpClientHandler).Information("{Token}", "FCD_USER_HTTPCLIENT_INFO"),
            "FCD_USER_HTTPCLIENT_INFO");

        Assert.Equal(1, counts["FCD_USER_HTTPCLIENT_INFO"]);
    }

    [Fact]
    public void UserFloor_OverridingADefault_IsKeptInTheBuffer()
    {
        var counts = Run(
            new() { ["SerilogStepUp:CategoryFloors:Microsoft.AspNetCore.Routing"] = "Information" },
            logger =>
            {
                logger.ForContext("SourceContext", Routing).Information("{Token}", "FCD_USER_ROUTING_INFO");
                logger.Error("{Token}", "FCD_ERR");
            },
            "FCD_USER_ROUTING_INFO");

        Assert.Equal(1, counts["FCD_USER_ROUTING_INFO"]);
    }

    [Fact]
    public void AlwaysOn_FrameworkInformation_IsExported()
    {
        var counts = Run(
            new() { ["SerilogStepUp:Mode"] = "AlwaysOn" },
            logger => logger.ForContext("SourceContext", Hosting).Information("{Token}", "FCD_ALWAYSON_HOSTING_INFO"),
            "FCD_ALWAYSON_HOSTING_INFO");

        Assert.Equal(1, counts["FCD_ALWAYSON_HOSTING_INFO"]);
    }

    [Fact]
    public void Diagnostic_FrameworkDebug_IsExported()
    {
        var counts = Run(
            new() { ["SerilogStepUp:Mode"] = "Diagnostic" },
            logger => logger.ForContext("SourceContext", Routing).Debug("{Token}", "FCD_DIAG_ROUTING_DEBUG"),
            "FCD_DIAG_ROUTING_DEBUG");

        Assert.Equal(1, counts["FCD_DIAG_ROUTING_DEBUG"]);
    }

    [Fact]
    public void Diagnostic_ExemptDefaultKey_KeepsItsFloor()
    {
        var counts = Run(
            new()
            {
                ["SerilogStepUp:Mode"] = "Diagnostic",
                ["SerilogStepUp:DiagnosticExemptCategories:0"] = "Microsoft.AspNetCore.Routing",
            },
            logger => logger.ForContext("SourceContext", Routing).Debug("{Token}", "FCD_DIAG_EXEMPT_ROUTING_DEBUG"),
            "FCD_DIAG_EXEMPT_ROUTING_DEBUG");

        Assert.Equal(0, counts["FCD_DIAG_EXEMPT_ROUTING_DEBUG"]);
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
