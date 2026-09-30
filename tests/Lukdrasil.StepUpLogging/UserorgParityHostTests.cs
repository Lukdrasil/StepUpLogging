using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Tests;

public class UserorgParityHostTests
{
    [Fact]
    public void UserorgParityConfiguration_ThroughTheConfigureOptionsOverload_HoldsEveryGoalAcrossDiagnosticAndAuto()
    {
        var configureRan = false;
        using var activity = new Activity("userorg-parity");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();

        using var run = DiagnosticModeTests.DiagnosticHost.Start(
            [("Mode", "Auto"), ("BaseLevel", "Error")],
            registerFakeTime: true,
            builder => builder.AddStepUpLogging(ConfigureUserorgParity, (_, _) => configureRan = true));
        var controller = run.Controller;
        Serilog.ILogger For(string category) => run.Logger.ForContext("SourceContext", category);

        var opts = run.Host.Services.GetRequiredService<IOptions<StepUpLoggingOptions>>().Value;
        Assert.True(configureRan);
        Assert.Equal(StepUpMode.Diagnostic, opts.Mode);
        Assert.Equal("Warning", opts.CategoryFloors["Microsoft"]);
        Assert.Equal(["Polly"], opts.NeverTriggerCategories);

        Assert.Equal(LogEventLevel.Debug, controller.LevelSwitch.MinimumLevel);

        For("Microsoft.AspNetCore.Hosting").Information("{Token}", "PARITY_ASPNETCORE_DIAGNOSTIC");
        For("Microsoft.EntityFrameworkCore.Query").Information("{Token}", "PARITY_EFQUERY_DIAGNOSTIC");
        Assert.Equal(1, run.Count("PARITY_ASPNETCORE_DIAGNOSTIC"));
        Assert.Equal(0, run.Count("PARITY_EFQUERY_DIAGNOSTIC"));

        run.Time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(LogEventLevel.Warning, controller.LevelSwitch.MinimumLevel);

        For("Microsoft.Hosting.Lifetime").Information("{Token}", "PARITY_LIFETIME");
        Assert.Equal(1, run.Count("PARITY_LIFETIME"));

        For("Polly").Error("{Token}", "PARITY_POLLY_ERROR");
        Assert.Equal(1, run.Count("PARITY_POLLY_ERROR"));
        Assert.False(SpinWait.SpinUntil(() => controller.IsSteppedUp, 500));

        controller.Trigger();
        Assert.Equal(LogEventLevel.Information, controller.LevelSwitch.MinimumLevel);
        For("Microsoft.AspNetCore.Hosting").Information("{Token}", "PARITY_ASPNETCORE_AUTO");
        Assert.Equal(0, run.Count("PARITY_ASPNETCORE_AUTO"));
    }

    private static void ConfigureUserorgParity(StepUpLoggingOptions options)
    {
        options.Mode = StepUpMode.Diagnostic;
        options.BaseLevel = "Warning";
        options.StepUpLevel = "Information";
        options.DiagnosticLevel = "Debug";
        options.DiagnosticDurationMinutes = 30;
        options.EnablePreErrorBuffering = true;
        options.CategoryFloors = new(StringComparer.Ordinal)
        {
            ["Microsoft"] = "Warning",
            ["System"] = "Warning",
        };
        options.DiagnosticExemptCategories =
        [
            "Microsoft.EntityFrameworkCore.Database.Command",
            "Microsoft.EntityFrameworkCore.ChangeTracking",
            "Microsoft.EntityFrameworkCore.Query",
            "Microsoft.EntityFrameworkCore.Update",
            "Microsoft.Extensions.Hosting.Diagnostics",
        ];
        options.AlwaysExportCategories = ["Microsoft.Hosting.Lifetime"];
        options.NeverTriggerCategories = ["Polly"];
    }
}
