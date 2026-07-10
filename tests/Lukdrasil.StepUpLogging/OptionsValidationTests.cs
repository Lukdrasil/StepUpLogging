using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

public class OptionsValidationTests
{
    private static IHost BuildHost(params (string Key, string Value)[] settings)
    {
        var dict = new Dictionary<string, string?>
        {
            // Keep OTLP machinery out of the test host.
            ["SerilogStepUp:EnableOtlpExporter"] = "false",
        };
        foreach (var (key, value) in settings)
        {
            dict[key] = value;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(dict);
        builder.AddStepUpLogging();
        return builder.Build();
    }

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
}
