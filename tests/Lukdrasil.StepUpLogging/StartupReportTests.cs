using System;
using System.Linq;
using Serilog.Events;
using DiagnosticHost = Lukdrasil.StepUpLogging.Tests.DiagnosticModeTests.DiagnosticHost;

namespace Lukdrasil.StepUpLogging.Tests;

public class StartupReportTests
{
    [Fact]
    public void Start_WritesOneInformationSummaryOfTheEffectiveConfiguration()
    {
        using var run = Start();

        var summary = Assert.Single(run.WithTemplate(StartupReport.SummaryTemplate));
        Assert.Equal(LogEventLevel.Information, summary.Level);
        var text = summary.RenderMessage();
        Assert.Contains("Mode Auto", text, StringComparison.Ordinal);
        Assert.Contains("BaseLevel Warning", text, StringComparison.Ordinal);
        Assert.Contains("StepUpLevel Information for 300 s", text, StringComparison.Ordinal);
        Assert.Contains("framework defaults Recommended", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownKey_IsWarnedByItsFullName()
    {
        using var run = Start(("OtlpEndpoint", "http://collector:4317"));

        var warning = Assert.Single(run.WithTemplate(StartupReport.UnknownKeyTemplate));
        Assert.Equal(LogEventLevel.Warning, warning.Level);
        Assert.Contains("SerilogStepUp:OtlpEndpoint", warning.RenderMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void KnownKeys_InAnyCaseAndWithChildren_AreNotWarned()
    {
        using var run = Start(("enableconsolelogging", "false"), ("CategoryFloors:MyApp", "Warning"), ("RedactionRegexes:0", "secret"));

        Assert.Empty(run.WithTemplate(StartupReport.UnknownKeyTemplate));
    }

    [Fact]
    public void ConsoleTextFormat_WithTheConsoleOff_IsWarnedAsUnused()
    {
        using var run = Start(("ConsoleFormat", "Text"));

        Assert.Contains(run.WithTemplate(StartupReport.UnusedOptionTemplate), e => e.RenderMessage().Contains("ConsoleFormat", StringComparison.Ordinal));
    }

    [Fact]
    public void RedactLogEventProperties_WithoutPatterns_IsWarnedAsUnused()
    {
        using var run = Start(("RedactLogEventProperties", "true"));

        Assert.Contains(run.WithTemplate(StartupReport.UnusedOptionTemplate), e => e.RenderMessage().Contains("RedactLogEventProperties", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultConfiguration_WarnsNothing()
    {
        using var run = Start();

        Assert.DoesNotContain(run.Events(), e => e.Level >= LogEventLevel.Warning);
    }

    [Theory]
    [InlineData("http://collector:4317", "http://collector:4317")]
    [InlineData("https://user:secret@collector:4318/v1/logs?token=abc", "https://collector:4318")]
    [InlineData(null, "default localhost")]
    public void OtlpEndpoint_IsReportedWithoutCredentialsPathOrQuery(string? endpoint, string expected)
    {
        Assert.Equal(expected, StartupReport.OtlpEndpoint(endpoint));
    }

    private static DiagnosticHost Start(params (string Key, string Value)[] overrides)
        => DiagnosticHost.Start([("Mode", "Auto"), .. overrides], registerFakeTime: false);
}
