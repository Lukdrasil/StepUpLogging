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

    [Theory]
    [InlineData("99")]
    [InlineData("1")]
    [InlineData("Debug,Error")]
    public void LevelStringThatIsNotALevelName_FailsValidationOnStart(string level)
    {
        Assert.Throws<OptionsValidationException>(
            () => BuildHost(("SerilogStepUp:BaseLevel", level)));
    }

    [Theory]
    [InlineData("Mode")]
    [InlineData("FrameworkCategoryDefaults")]
    [InlineData("ConsoleFormat")]
    public void UndefinedEnumNumber_FailsValidationOnStart(string option)
    {
        Assert.Throws<OptionsValidationException>(
            () => BuildHost(("SerilogStepUp:" + option, "5")));
    }

    [Theory]
    [InlineData("BaseLevel", "Warnign")]
    [InlineData("Mode", "5")]
    [InlineData("DurationSeconds", "0")]
    [InlineData("PreErrorBufferSize", "0")]
    [InlineData("MaxContinuousStepUpSeconds", "-1")]
    public void InvalidOption_FailsValidationNamingOnlyThatOption(string option, string value)
    {
        var ex = Assert.Throws<OptionsValidationException>(
            () => BuildHost(("SerilogStepUp:" + option, value)));

        var failure = Assert.Single(ex.Failures);
        Assert.StartsWith("SerilogStepUp:" + option + " ", failure);
    }

    [Fact]
    public void TwoInvalidOptions_FailValidationNamingBoth()
    {
        var ex = Assert.Throws<OptionsValidationException>(
            () => BuildHost(("SerilogStepUp:BaseLevel", "Warnign"), ("SerilogStepUp:DurationSeconds", "0")));

        Assert.Equal(2, ex.Failures.Count());
    }

    [Fact]
    public void InvalidRedactionRegex_FailsValidationNamingThePattern()
    {
        var ex = Assert.Throws<OptionsValidationException>(
            () => BuildHost(("SerilogStepUp:RedactionRegexes:0", "password=(")));

        var failure = Assert.Single(ex.Failures);
        Assert.Contains("RedactionRegexes", failure);
        Assert.Contains("password=(", failure);
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

        Assert.Equal(1, opts.DurationSeconds);
        Assert.Equal("Warning", opts.BaseLevel);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("http://collector:4317", true)]
    public void EnableOtlpExporter_DefaultsToWhetherAnEndpointIsSet(string? endpoint, bool expected)
    {
        Assert.Equal(expected, StepUpLoggingOptions.OtlpEndpointIsSet(endpoint));
    }

    [Fact]
    public void NeverStepUpCategoriesNotConfigured_ResolvesToEfDatabaseCommand()
    {
        Assert.Equal(["Microsoft.EntityFrameworkCore.Database.Command"], ResolveFromJson("", o => o.NeverStepUpCategories!));
    }

    [Fact]
    public void NeverStepUpCategoriesConfigured_ReplacesTheDefault()
    {
        Assert.Equal(["MyApp.Noisy"], ResolveFromJson(""", "NeverStepUpCategories": ["MyApp.Noisy"]""", o => o.NeverStepUpCategories!));
    }

    [Fact]
    public void NeverStepUpCategoriesConfiguredEmpty_PinsNothing()
    {
        Assert.Equal([], ResolveFromJson(""", "NeverStepUpCategories": []""", o => o.NeverStepUpCategories!));
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
    public void PreErrorBufferExcludeCategories_DefaultsToEmpty()
    {
        Assert.Empty(new StepUpLoggingOptions().PreErrorBufferExcludeCategories);
    }

    [Fact]
    public void PreErrorBufferExcludeCategories_BindsFromConfig()
    {
        using var host = BuildHost(
            ("SerilogStepUp:PreErrorBufferExcludeCategories:0", "Microsoft.AspNetCore.Routing"),
            ("SerilogStepUp:PreErrorBufferExcludeCategories:1", "Polly"));

        Assert.Equal(["Microsoft.AspNetCore.Routing", "Polly"], ResolveOptions(host).PreErrorBufferExcludeCategories);
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
    public void ExemptCategoryUnderAFrameworkDefaultKey_ResolvesOnStart()
    {
        using var host = BuildHost(("SerilogStepUp:DiagnosticExemptCategories:0", "Microsoft.AspNetCore.Routing"));

        Assert.Equal(["Microsoft.AspNetCore.Routing"], ResolveOptions(host).DiagnosticExemptCategories);
    }

    [Fact]
    public void ExemptCategoryUnderAFrameworkDefaultKey_WithPresetNone_FailsValidationOnStart()
    {
        Assert.Throws<OptionsValidationException>(
            () => BuildHost(
                ("SerilogStepUp:FrameworkCategoryDefaults", "None"),
                ("SerilogStepUp:DiagnosticExemptCategories:0", "Microsoft.AspNetCore.Routing")));
    }

    [Fact]
    public void FrameworkCategoryDefaults_DefaultsToRecommended()
    {
        Assert.Equal(FrameworkCategoryPreset.Recommended, new StepUpLoggingOptions().FrameworkCategoryDefaults);
    }

    [Fact]
    public void FrameworkCategoryDefaults_BindsNoneFromConfig()
    {
        using var host = BuildHost(("SerilogStepUp:FrameworkCategoryDefaults", "None"));

        Assert.Equal(FrameworkCategoryPreset.None, ResolveOptions(host).FrameworkCategoryDefaults);
    }

    [Fact]
    public void FrameworkCategoryDefaults_CodeOverridesConfig()
    {
        using var host = BuildHostWith(
            o => o.FrameworkCategoryDefaults = FrameworkCategoryPreset.Recommended,
            ("SerilogStepUp:FrameworkCategoryDefaults", "None"));

        Assert.Equal(FrameworkCategoryPreset.Recommended, ResolveOptions(host).FrameworkCategoryDefaults);
    }

    [Fact]
    public void ConsoleFormat_BindsTextFromConfig()
    {
        using var host = BuildHost(("SerilogStepUp:ConsoleFormat", "Text"));

        Assert.Equal(ConsoleLogFormat.Text, ResolveOptions(host).ConsoleFormat);
    }

    [Fact]
    public void ConsoleTextProperties_NotConfigured_ResolvesToJti()
    {
        using var host = BuildHost();

        Assert.Equal(["Jti"], ResolveOptions(host).ConsoleTextProperties!);
    }

    [Fact]
    public void ConsoleTextProperties_Configured_ReplacesTheDefault()
    {
        using var host = BuildHost(("SerilogStepUp:ConsoleTextProperties:0", "UserId"));

        Assert.Equal(["UserId"], ResolveOptions(host).ConsoleTextProperties!);
    }

    [Fact]
    public void ConsoleText_WiresWithoutError()
    {
        using var host = BuildHost(("SerilogStepUp:EnableConsoleLogging", "true"), ("SerilogStepUp:ConsoleFormat", "Text"));

        Assert.Equal(ConsoleLogFormat.Text, ResolveOptions(host).ConsoleFormat);
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
            ("SerilogStepUp:NeverTriggerCategories:1", "   "),
            ("SerilogStepUp:PreErrorBufferExcludeCategories:0", ""),
            ("SerilogStepUp:PreErrorBufferExcludeCategories:1", "   "));

        var opts = ResolveOptions(host);

        Assert.Equal(2, opts.DiagnosticExemptCategories.Length);
        Assert.Equal(2, opts.AlwaysExportCategories.Length);
        Assert.Equal(2, opts.NeverTriggerCategories.Length);
        Assert.Equal(2, opts.PreErrorBufferExcludeCategories.Length);
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
            o.PreErrorBufferExcludeCategories = null!;
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
        => ResolveFromJson(sectionJson, o => o.ExcludePaths, configureOptions);

    private static T ResolveFromJson<T>(string sectionJson, Func<StepUpLoggingOptions, T> select, Action<StepUpLoggingOptions>? configureOptions = null)
    {
        var json = $$"""{ "SerilogStepUp": { "EnableOtlpExporter": false {{sectionJson}} } }""";
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        builder.AddStepUpLogging(configureOptions);
        using var host = builder.Build();
        return select(host.Services.GetRequiredService<IOptions<StepUpLoggingOptions>>().Value);
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
