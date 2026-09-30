using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

public class OptionsValidationTests
{
    private static IHost BuildHost(params (string Key, string Value)[] settings)
        => BuildHostWith(_ => { }, settings);

    [Fact]
    public void InvalidLevelString_FailsValidationOnStart()
    {
        Assert.Throws<OptionsValidationException>(
            () => BuildHost(("SerilogStepUp:BaseLevel", "Warnign")));
    }

    [Fact]
    public void NonPositiveDuration_FailsValidationOnStart()
    {
        Assert.Throws<OptionsValidationException>(
            () => BuildHost(("SerilogStepUp:DurationSeconds", "0")));
    }

    [Fact]
    public void ZeroMaxBodyCaptureBytes_FailsValidationOnStart()
    {
        Assert.Throws<OptionsValidationException>(
            () => BuildHost(("SerilogStepUp:MaxBodyCaptureBytes", "0")));
    }

    [Fact]
    public void NegativeMaxBodyCaptureBytes_FailsValidationOnStart()
    {
        Assert.Throws<OptionsValidationException>(
            () => BuildHost(("SerilogStepUp:MaxBodyCaptureBytes", "-1")));
    }

    [Fact]
    public void PositiveMaxBodyCaptureBytes_ResolvesOnStart()
    {
        using var host = BuildHost(("SerilogStepUp:MaxBodyCaptureBytes", "1"));

        var opts = host.Services.GetRequiredService<IOptions<StepUpLoggingOptions>>().Value;

        Assert.Equal(1, opts.MaxBodyCaptureBytes);
    }

    [Fact]
    public void CapBelowDuration_FailsValidationOnStart()
    {
        Assert.Throws<OptionsValidationException>(
            () => BuildHost(
                ("SerilogStepUp:DurationSeconds", "180"),
                ("SerilogStepUp:MaxContinuousStepUpSeconds", "60")));
    }

    [Fact]
    public void CapDisabled_ResolvesOnStart()
    {
        using var host = BuildHost(("SerilogStepUp:MaxContinuousStepUpSeconds", "0"));

        var opts = host.Services.GetRequiredService<IOptions<StepUpLoggingOptions>>().Value;

        Assert.Equal(0, opts.MaxContinuousStepUpSeconds);
    }

    [Fact]
    public void CapEqualToDuration_ResolvesOnStart()
    {
        using var host = BuildHost(
            ("SerilogStepUp:DurationSeconds", "180"),
            ("SerilogStepUp:MaxContinuousStepUpSeconds", "180"));

        var opts = host.Services.GetRequiredService<IOptions<StepUpLoggingOptions>>().Value;

        Assert.Equal(180, opts.MaxContinuousStepUpSeconds);
    }

    [Fact]
    public void NegativeCooldown_FailsValidationOnStart()
    {
        Assert.Throws<OptionsValidationException>(
            () => BuildHost(("SerilogStepUp:StepUpCooldownSeconds", "-1")));
    }

    [Fact]
    public void ZeroPreErrorBufferSize_FailsValidationOnStart()
    {
        Assert.Throws<OptionsValidationException>(
            () => BuildHost(("SerilogStepUp:PreErrorBufferSize", "0")));
    }

    [Fact]
    public void NegativePreErrorBufferSize_FailsValidationOnStart()
    {
        Assert.Throws<OptionsValidationException>(
            () => BuildHost(("SerilogStepUp:PreErrorBufferSize", "-1")));
    }

    [Fact]
    public void ZeroPreErrorMaxContexts_FailsValidationOnStart()
    {
        Assert.Throws<OptionsValidationException>(
            () => BuildHost(("SerilogStepUp:PreErrorMaxContexts", "0")));
    }

    [Fact]
    public void NegativePreErrorMaxContexts_FailsValidationOnStart()
    {
        Assert.Throws<OptionsValidationException>(
            () => BuildHost(("SerilogStepUp:PreErrorMaxContexts", "-1")));
    }

    [Fact]
    public void PositivePreErrorBufferBounds_ResolveOnStart()
    {
        using var host = BuildHost(
            ("SerilogStepUp:PreErrorBufferSize", "5"),
            ("SerilogStepUp:PreErrorMaxContexts", "7"));

        var opts = host.Services.GetRequiredService<IOptions<StepUpLoggingOptions>>().Value;

        Assert.Equal(5, opts.PreErrorBufferSize);
        Assert.Equal(7, opts.PreErrorMaxContexts);
    }

    [Fact]
    public void ValidConfiguration_ResolvesWithCanonicalDefaults()
    {
        using var host = BuildHost();

        var opts = host.Services.GetRequiredService<IOptions<StepUpLoggingOptions>>().Value;

        Assert.Equal(180, opts.DurationSeconds);
        Assert.Equal("Warning", opts.BaseLevel);
    }

    [Fact]
    public void NeverStepUpCategories_DefaultsToEfDatabaseCommand()
    {
        Assert.Equal(
            new[] { "Microsoft.EntityFrameworkCore.Database.Command" },
            new StepUpLoggingOptions().NeverStepUpCategories);
    }

    private static IHost BuildHostWith(Action<StepUpLoggingOptions> configureOptions, params (string Key, string Value)[] settings)
    {
        var dict = new Dictionary<string, string?>
        {
            ["SerilogStepUp:EnableOtlpExporter"] = "false",
        };
        foreach (var (key, value) in settings)
        {
            dict[key] = value;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(dict);
        builder.AddStepUpLogging(configureOptions);
        return builder.Build();
    }

    private static StepUpLoggingOptions ResolveOptions(IHost host)
        => host.Services.GetRequiredService<IOptions<StepUpLoggingOptions>>().Value;

    [Fact]
    public void CategoryOptions_HaveDocumentedDefaults()
    {
        var opts = new StepUpLoggingOptions();

        Assert.Empty(opts.CategoryFloors);
        Assert.Empty(opts.DiagnosticExemptCategories);
        Assert.Equal("Debug", opts.DiagnosticLevel);
        Assert.Equal(30, opts.DiagnosticDurationMinutes);
        Assert.Empty(opts.AlwaysExportCategories);
        Assert.Empty(opts.NeverTriggerCategories);
        Assert.Equal(3, (int)StepUpMode.Diagnostic);
    }

    [Fact]
    public void CategoryFloors_DefaultKeysAreOrdinal()
    {
        var floors = new StepUpLoggingOptions().CategoryFloors;

        floors["Microsoft.AspNetCore"] = "Warning";

        Assert.False(floors.ContainsKey("microsoft.aspnetcore"));
    }

    [Fact]
    public void CategoryFloorInvalidLevel_FailsValidationOnStart()
    {
        Assert.Throws<OptionsValidationException>(
            () => BuildHost(("SerilogStepUp:CategoryFloors:Contoso.Payments", "Warnign")));
    }

    [Theory]
    [InlineData("Error")]
    [InlineData("Fatal")]
    public void CategoryFloorAboveWarning_FailsValidationNamingTheKey(string level)
    {
        var ex = Assert.Throws<OptionsValidationException>(
            () => BuildHost(("SerilogStepUp:CategoryFloors:Contoso.Payments", level)));

        Assert.Contains("Contoso.Payments", ex.Message);
    }

    [Fact]
    public void CategoryFloorWarning_ResolvesOnStart()
    {
        using var host = BuildHost(("SerilogStepUp:CategoryFloors:Contoso.Payments", "Warning"));

        Assert.Equal("Warning", ResolveOptions(host).CategoryFloors["Contoso.Payments"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankCategoryFloorKey_FailsValidationOnStart(string key)
    {
        Assert.Throws<OptionsValidationException>(
            () => BuildHostWith(o => o.CategoryFloors[key] = "Warning"));
    }

    [Fact]
    public void InvalidCategoryFloor_FailsValidationInAlwaysOnMode()
    {
        Assert.Throws<OptionsValidationException>(
            () => BuildHost(
                ("SerilogStepUp:Mode", "AlwaysOn"),
                ("SerilogStepUp:CategoryFloors:Contoso.Payments", "Error")));
    }

    [Fact]
    public void InvalidDiagnosticLevel_FailsValidationOnStart()
    {
        Assert.Throws<OptionsValidationException>(
            () => BuildHost(("SerilogStepUp:DiagnosticLevel", "Verbos")));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("121")]
    public void DiagnosticDurationOutOfRange_FailsValidationOnStart(string minutes)
    {
        var ex = Assert.Throws<OptionsValidationException>(
            () => BuildHost(("SerilogStepUp:DiagnosticDurationMinutes", minutes)));

        Assert.Contains("DiagnosticDurationMinutes must be 1-120", ex.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(120)]
    public void DiagnosticDurationInRange_ResolvesOnStart(int minutes)
    {
        using var host = BuildHost(("SerilogStepUp:DiagnosticDurationMinutes", minutes.ToString()));

        Assert.Equal(minutes, ResolveOptions(host).DiagnosticDurationMinutes);
    }

    [Fact]
    public void ExemptCategoryWithoutMatchingFloorKey_FailsValidationOnStart()
    {
        Assert.Throws<OptionsValidationException>(
            () => BuildHost(
                ("SerilogStepUp:CategoryFloors:Microsoft.AspNetCore", "Warning"),
                ("SerilogStepUp:DiagnosticExemptCategories:0", "Microsoft.EntityFrameworkCore")));
    }

    [Fact]
    public void ExemptCategoryBroaderThanFloorKey_FailsValidationOnStart()
    {
        Assert.Throws<OptionsValidationException>(
            () => BuildHost(
                ("SerilogStepUp:CategoryFloors:Microsoft.EntityFrameworkCore.Database", "Warning"),
                ("SerilogStepUp:DiagnosticExemptCategories:0", "Microsoft.EntityFrameworkCore")));
    }

    [Fact]
    public void ExemptCategoryNarrowerThanFloorKey_ResolvesOnStart()
    {
        using var host = BuildHost(
            ("SerilogStepUp:CategoryFloors:Microsoft.EntityFrameworkCore", "Warning"),
            ("SerilogStepUp:DiagnosticExemptCategories:0", "Microsoft.EntityFrameworkCore.Database.Command"));

        Assert.Equal(["Microsoft.EntityFrameworkCore.Database.Command"], ResolveOptions(host).DiagnosticExemptCategories);
    }

    [Fact]
    public void BlankCategoryListEntries_ResolveOnStart()
    {
        using var host = BuildHost(
            ("SerilogStepUp:DiagnosticExemptCategories:0", ""),
            ("SerilogStepUp:DiagnosticExemptCategories:1", "   "),
            ("SerilogStepUp:AlwaysExportCategories:0", ""),
            ("SerilogStepUp:AlwaysExportCategories:1", "   "),
            ("SerilogStepUp:NeverTriggerCategories:0", ""),
            ("SerilogStepUp:NeverTriggerCategories:1", "   "));

        var opts = ResolveOptions(host);

        Assert.Equal(2, opts.DiagnosticExemptCategories.Length);
        Assert.Equal(2, opts.AlwaysExportCategories.Length);
        Assert.Equal(2, opts.NeverTriggerCategories.Length);
    }

    [Fact]
    public void NullCategoryFloorsAndLists_ResolveOnStart()
    {
        using var host = BuildHostWith(o =>
        {
            o.CategoryFloors = null!;
            o.DiagnosticExemptCategories = null!;
            o.AlwaysExportCategories = null!;
            o.NeverTriggerCategories = null!;
        });

        Assert.Null(ResolveOptions(host).CategoryFloors);
        host.Services.GetRequiredService<Serilog.ILogger>();
    }

    [Fact]
    public void DiagnosticMode_BindsFromConfigAndTriggerLeavesTheSwitchAtDiagnosticLevel()
    {
        using var host = BuildHost(("SerilogStepUp:Mode", "Diagnostic"));
        var controller = host.Services.GetRequiredService<StepUpLoggingController>();

        controller.Trigger();

        Assert.Equal(StepUpMode.Diagnostic, ResolveOptions(host).Mode);
        Assert.True(controller.IsSteppedUp);
        Assert.Equal(Serilog.Events.LogEventLevel.Debug, controller.LevelSwitch.MinimumLevel);
    }

    private static IEnumerable<string>? ResolveExcludePaths(string sectionJson, Action<StepUpLoggingOptions>? configureOptions = null)
    {
        var json = $$"""{ "SerilogStepUp": { "EnableOtlpExporter": false {{sectionJson}} } }""";
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        builder.AddStepUpLogging(configureOptions);
        using var host = builder.Build();
        return host.Services.GetRequiredService<IOptions<StepUpLoggingOptions>>().Value.ExcludePaths;
    }

    [Fact]
    public void ExcludePathsNotConfigured_ResolvesToBuiltInDefaults()
    {
        Assert.Equal(["/healthz", "/metrics", "/health"], ResolveExcludePaths(""));
    }

    [Fact]
    public void ExcludePathsConfigured_ReplacesBuiltInDefaults()
    {
        Assert.Equal(["/health", "/alive"], ResolveExcludePaths(""", "ExcludePaths": ["/health", "/alive"]"""));
    }

    [Fact]
    public void ExcludePathsSetInCode_WinsOverConfiguration()
    {
        var resolved = ResolveExcludePaths(
            """, "ExcludePaths": ["/health", "/alive"]""",
            o => o.ExcludePaths = ["/ready"]);

        Assert.Equal(["/ready"], resolved);
    }

    [Fact]
    public void ExcludePathsConfiguredEmpty_ExcludesNothing()
    {
        Assert.Equal([], ResolveExcludePaths(""", "ExcludePaths": []"""));
    }
}
