using System;
using System.Collections.Generic;
using System.Linq;
using Serilog.Events;
using Serilog.Parsing;

namespace Lukdrasil.StepUpLogging.Tests;

public class CategoryPolicyTests
{
    private const string Routing = "Microsoft.AspNetCore.Routing";
    private const string Cors = "Microsoft.AspNetCore.Cors";

    private static CategoryPolicy Policy(Action<StepUpLoggingOptions>? configure = null)
    {
        var options = new StepUpLoggingOptions { NeverStepUpCategories = [], FrameworkCategoryDefaults = FrameworkCategoryPreset.None };
        configure?.Invoke(options);
        return new CategoryPolicy(options);
    }

    private static CategoryPolicy Floors(params (string Key, string Floor)[] floors) => Floors(floors, []);

    private static CategoryPolicy Floors((string Key, string Floor)[] floors, string[] exempt) => Policy(o =>
    {
        o.CategoryFloors = floors.ToDictionary(f => f.Key, f => f.Floor, StringComparer.Ordinal);
        o.DiagnosticExemptCategories = exempt;
    });

    private static LogEvent Event(string? source, LogEventLevel level = LogEventLevel.Verbose)
    {
        LogEventProperty[] properties = source is null ? [] : [new LogEventProperty("SourceContext", new ScalarValue(source))];
        return new LogEvent(DateTimeOffset.UtcNow, level, null, new MessageTemplateParser().Parse("m"), properties);
    }

    private static CategoryVerdict Verdict(CategoryPolicy policy, string? source, LogEventLevel level = LogEventLevel.Verbose, LogEventLevel switchLevel = LogEventLevel.Verbose, bool diagnosticActive = false)
        => policy.Resolve(Event(source, level), switchLevel, () => diagnosticActive);

    private static LogEventLevel MinimumOf(CategoryPolicy policy, string source, bool diagnosticActive = false)
        => Verdict(policy, source, diagnosticActive: diagnosticActive).ExportMinimum;

    [Fact]
    public void NoRules_ReturnsTheSwitchLevel_WithoutReadingDiagnostic()
    {
        var reads = 0;

        var verdict = Policy().Resolve(Event("Any.Category"), LogEventLevel.Information, () => { reads++; return true; });

        Assert.Equal(new CategoryVerdict(LogEventLevel.Information, false), verdict);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void NoSourceContext_ReturnsTheSwitchLevel()
    {
        var policy = Floors(("Test", "Warning"));

        Assert.Equal(new CategoryVerdict(LogEventLevel.Debug, false), Verdict(policy, null, switchLevel: LogEventLevel.Debug));
    }

    [Fact]
    public void ExactKey_GetsItsFloor()
    {
        Assert.Equal(LogEventLevel.Warning, MinimumOf(Floors(("Test.Floored", "Warning")), "Test.Floored"));
    }

    [Fact]
    public void DotChildOfKey_GetsItsFloor()
    {
        Assert.Equal(LogEventLevel.Warning, MinimumOf(Floors(("Test.Floored", "Warning")), "Test.Floored.Child"));
    }

    [Fact]
    public void KeyWithoutDotBoundary_DoesNotMatch()
    {
        var policy = Floors(("Test.Floored", "Warning"));

        Assert.Equal(LogEventLevel.Verbose, MinimumOf(policy, "Test.FlooredX"));
        Assert.Equal(LogEventLevel.Verbose, MinimumOf(policy, "Test"));
    }

    [Fact]
    public void MatchIsOrdinalCaseSensitive()
    {
        Assert.Equal(LogEventLevel.Verbose, MinimumOf(Floors(("Test.Floored", "Warning")), "test.floored"));
    }

    [Fact]
    public void FloorLevelIsParsedIgnoringCase()
    {
        var policy = Floors(("A", "debug"), ("B", "Information"));

        Assert.Equal(LogEventLevel.Debug, MinimumOf(policy, "A.X"));
        Assert.Equal(LogEventLevel.Information, MinimumOf(policy, "B.X"));
    }

    [Fact]
    public void BlankFloorKey_IsIgnored()
    {
        Assert.Equal(LogEventLevel.Verbose, MinimumOf(Floors((" ", "Warning")), ".Rooted"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MostSpecificMatchingKeyDecides_WhateverTheConfiguredOrder(bool reversed)
    {
        (string, string)[] floors = [("Test", "Warning"), ("Test.Floored", "Information")];
        var policy = Floors(reversed ? [.. floors.Reverse()] : floors);

        Assert.Equal(LogEventLevel.Information, MinimumOf(policy, "Test.Floored.Child"));
        Assert.Equal(LogEventLevel.Information, MinimumOf(policy, "Test.Floored"));
        Assert.Equal(LogEventLevel.Warning, MinimumOf(policy, "Test.Other"));
        Assert.Equal(LogEventLevel.Warning, MinimumOf(policy, "Test"));
        Assert.Equal(LogEventLevel.Verbose, MinimumOf(policy, "Unrelated"));
    }

    [Fact]
    public void FloorOnlyRaisesTheSwitchLevel()
    {
        var policy = Floors(("A", "Debug"));

        Assert.Equal(LogEventLevel.Information, Verdict(policy, "A.X", switchLevel: LogEventLevel.Information).ExportMinimum);
    }

    [Fact]
    public void WarningAndAbove_IgnoreTheFloor()
    {
        var policy = Floors(("A", "Error"));

        Assert.Equal(LogEventLevel.Verbose, Verdict(policy, "A.X", LogEventLevel.Warning).ExportMinimum);
    }

    [Fact]
    public void Diagnostic_LiftsANonExemptFloor()
    {
        var policy = Floors(("A", "Warning"));

        Assert.Equal(LogEventLevel.Verbose, MinimumOf(policy, "A.X", diagnosticActive: true));
        Assert.Equal(LogEventLevel.Warning, MinimumOf(policy, "A.X", diagnosticActive: false));
    }

    [Fact]
    public void Diagnostic_KeepsAnExemptFloor_AndLiftsTheOthers()
    {
        var policy = Floors([("A", "Warning"), ("C", "Information")], ["C"]);

        Assert.Equal(LogEventLevel.Information, MinimumOf(policy, "C.Y", diagnosticActive: true));
        Assert.Equal(LogEventLevel.Verbose, MinimumOf(policy, "A.Y", diagnosticActive: true));
    }

    [Fact]
    public void Diagnostic_ExemptNarrowerThanItsFloorKey_KeepsTheFloorOnlyUnderTheExemptPrefix()
    {
        var policy = Floors([("A", "Warning")], ["A.B"]);

        Assert.Equal(LogEventLevel.Warning, MinimumOf(policy, "A.B.C", diagnosticActive: true));
        Assert.Equal(LogEventLevel.Warning, MinimumOf(policy, "A.B", diagnosticActive: true));
        Assert.Equal(LogEventLevel.Verbose, MinimumOf(policy, "A.X", diagnosticActive: true));
        Assert.Equal(LogEventLevel.Verbose, MinimumOf(policy, "A", diagnosticActive: true));
        Assert.Equal(LogEventLevel.Verbose, MinimumOf(policy, "A.BC", diagnosticActive: true));
    }

    [Fact]
    public void Diagnostic_ExemptSource_GetsTheFloorOfTheMostSpecificMatchingKey()
    {
        var policy = Floors([("A", "Warning"), ("A.B", "Information")], ["A.B.C"]);

        Assert.Equal(LogEventLevel.Information, MinimumOf(policy, "A.B.C.D", diagnosticActive: true));
        Assert.Equal(LogEventLevel.Verbose, MinimumOf(policy, "A.B.X", diagnosticActive: true));
    }

    [Fact]
    public void NotDiagnostic_ExemptListDoesNotNarrowTheFloors()
    {
        var policy = Floors([("A", "Warning")], ["A.B"]);

        Assert.Equal(LogEventLevel.Warning, MinimumOf(policy, "A.X"));
        Assert.Equal(LogEventLevel.Warning, MinimumOf(policy, "A.B.C"));
    }

    [Fact]
    public void NeverStepUp_PinsToBaseLevel_WhileTheSwitchIsLower()
    {
        var policy = Policy(o => { o.NeverStepUpCategories = ["Pinned"]; o.BaseLevel = "Warning"; });

        Assert.Equal(LogEventLevel.Warning, Verdict(policy, "Pinned.Child", LogEventLevel.Information, LogEventLevel.Debug).ExportMinimum);
        Assert.Equal(LogEventLevel.Error, Verdict(policy, "Pinned.Child", LogEventLevel.Information, LogEventLevel.Error).ExportMinimum);
        Assert.Equal(LogEventLevel.Debug, Verdict(policy, "Other", LogEventLevel.Information, LogEventLevel.Debug).ExportMinimum);
    }

    [Fact]
    public void NeverStepUp_AppliesToWarningAndAbove()
    {
        var policy = Policy(o => { o.NeverStepUpCategories = ["Pinned"]; o.BaseLevel = "Error"; });

        Assert.Equal(LogEventLevel.Error, Verdict(policy, "Pinned", LogEventLevel.Warning, LogEventLevel.Debug).ExportMinimum);
    }

    [Fact]
    public void NeverStepUp_IsSkippedWhileDiagnosticIsActive()
    {
        var policy = Policy(o => o.NeverStepUpCategories = ["Pinned"]);

        Assert.Equal(LogEventLevel.Debug, Verdict(policy, "Pinned", LogEventLevel.Information, LogEventLevel.Debug, diagnosticActive: true).ExportMinimum);
    }

    [Fact]
    public void FrameworkDefault_AppliesWhenNoUserKeyMatches_AndKeepsTheSourceOutOfTheBuffer()
    {
        var policy = Policy(o => o.FrameworkCategoryDefaults = FrameworkCategoryPreset.Recommended);

        Assert.Equal(new CategoryVerdict(LogEventLevel.Warning, true), Verdict(policy, Routing + ".EndpointMiddleware", LogEventLevel.Information));
        Assert.Equal(new CategoryVerdict(LogEventLevel.Verbose, false), Verdict(policy, "App.Child", LogEventLevel.Information));
    }

    [Fact]
    public void FrameworkDefault_KeptInBuffer_IsNotKeptOut()
    {
        var policy = Policy(o => o.FrameworkCategoryDefaults = FrameworkCategoryPreset.Recommended);

        Assert.Equal(new CategoryVerdict(LogEventLevel.Warning, false), Verdict(policy, Cors, LogEventLevel.Information));
    }

    [Fact]
    public void UserKeyShorterThanTheDefaultKey_StillWins_AndIsNotKeptOut()
    {
        var policy = Policy(o =>
        {
            o.FrameworkCategoryDefaults = FrameworkCategoryPreset.Recommended;
            o.CategoryFloors = new(StringComparer.Ordinal) { ["System.Net"] = "Information" };
        });

        Assert.Equal(new CategoryVerdict(LogEventLevel.Information, false), Verdict(policy, "System.Net.Http.HttpClient.Default.LogicalHandler", LogEventLevel.Debug));
    }

    [Fact]
    public void Diagnostic_LiftsAFrameworkDefault_AndItsBufferExclusion_UnlessExempt()
    {
        var policy = Policy(o =>
        {
            o.FrameworkCategoryDefaults = FrameworkCategoryPreset.Recommended;
            o.DiagnosticExemptCategories = [Routing + ".Kept"];
        });

        Assert.Equal(new CategoryVerdict(LogEventLevel.Verbose, false), Verdict(policy, Routing + ".Other", LogEventLevel.Information, diagnosticActive: true));
        Assert.Equal(new CategoryVerdict(LogEventLevel.Warning, true), Verdict(policy, Routing + ".Kept.Child", LogEventLevel.Information, diagnosticActive: true));
    }

    [Fact]
    public void AlwaysOn_ClearsThePinAndTheFrameworkDefaults_ButKeepsUserFloors()
    {
        var policy = Policy(o =>
        {
            o.Mode = StepUpMode.AlwaysOn;
            o.NeverStepUpCategories = ["Pinned"];
            o.FrameworkCategoryDefaults = FrameworkCategoryPreset.Recommended;
            o.CategoryFloors = new(StringComparer.Ordinal) { ["App"] = "Warning" };
        });

        Assert.Equal(LogEventLevel.Debug, Verdict(policy, "Pinned", LogEventLevel.Information, LogEventLevel.Debug).ExportMinimum);
        Assert.Equal(new CategoryVerdict(LogEventLevel.Debug, false), Verdict(policy, Routing, LogEventLevel.Information, LogEventLevel.Debug));
        Assert.Equal(LogEventLevel.Warning, Verdict(policy, "App.X", LogEventLevel.Information, LogEventLevel.Debug).ExportMinimum);
    }

    [Fact]
    public void BufferExcluded_HeldBelowWarning_IsKeptOut_WarningIsNot()
    {
        var policy = Policy(o => o.PreErrorBufferExcludeCategories = ["Noisy"]);

        Assert.True(Verdict(policy, "Noisy.Child", LogEventLevel.Information, LogEventLevel.Warning).KeptOutOfBuffer);
        Assert.False(Verdict(policy, "Noisy.Child", LogEventLevel.Warning, LogEventLevel.Error).KeptOutOfBuffer);
        Assert.False(Verdict(policy, "Other", LogEventLevel.Information, LogEventLevel.Warning).KeptOutOfBuffer);
    }

    [Fact]
    public void BufferExcluded_ExportedEvent_IsNotKeptOut()
    {
        var policy = Policy(o => o.PreErrorBufferExcludeCategories = ["Noisy"]);

        Assert.Equal(new CategoryVerdict(LogEventLevel.Debug, false), Verdict(policy, "Noisy", LogEventLevel.Information, LogEventLevel.Debug));
    }

    [Fact]
    public void BufferExcluded_IsNotLiftedByDiagnostic()
    {
        var policy = Policy(o => o.PreErrorBufferExcludeCategories = ["Noisy"]);

        Assert.True(Verdict(policy, "Noisy", LogEventLevel.Debug, LogEventLevel.Information, diagnosticActive: true).KeptOutOfBuffer);
    }

    [Fact]
    public void NeverTriggers_MatchesListedPrefixes_AndIgnoresBlankEntries()
    {
        var policy = Policy(o => o.NeverTriggerCategories = ["Polly", " "]);

        Assert.True(policy.NeverTriggers(Event("Polly.Retry")));
        Assert.False(policy.NeverTriggers(Event("PollyX")));
        Assert.False(policy.NeverTriggers(Event(".Rooted")));
        Assert.False(policy.NeverTriggers(Event(null)));
    }

    [Fact]
    public void AlwaysExports_MatchesListedPrefixes()
    {
        var policy = Policy(o => o.AlwaysExportCategories = ["MyApp.Orders", ""]);

        Assert.True(policy.HasAlwaysExport);
        Assert.True(policy.AlwaysExports(Event("MyApp.Orders.Checkout")));
        Assert.False(policy.AlwaysExports(Event("MyApp.Other")));
    }

    [Fact]
    public void HasAlwaysExport_IsFalse_WhenOnlyBlankEntriesAreListed()
    {
        Assert.False(Policy(o => o.AlwaysExportCategories = [" "]).HasAlwaysExport);
    }

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CategoryPolicy(null!));
    }

    [Fact]
    public void Resolve_DoesNotAllocate_QS01()
    {
        var policy = Policy(o =>
        {
            o.CategoryFloors = Enumerable.Range(0, 20).ToDictionary(i => $"Floor{i}.Category", _ => "Warning", StringComparer.Ordinal);
            o.DiagnosticExemptCategories = [.. Enumerable.Range(0, 5).Select(i => $"Floor{i}.Category.Exempt")];
            o.NeverStepUpCategories = ["Floor7.Category"];
            o.PreErrorBufferExcludeCategories = ["Unrelated"];
            o.FrameworkCategoryDefaults = FrameworkCategoryPreset.Recommended;
        });
        LogEvent[] events = [.. new[] { "Floor3.Category.Exempt.Child", "Floor7.Category.Child", "Floor19.Category", "Unrelated.Category", "Floor1", Routing }
            .Select(s => Event(s, LogEventLevel.Debug))];
        Func<bool> notDiagnostic = () => false;
        Func<bool> diagnostic = () => true;

        var raised = 0;
        void Run()
        {
            for (var i = 0; i < 10_000; i++)
            {
                if (policy.Resolve(events[i % events.Length], LogEventLevel.Debug, (i & 1) == 0 ? diagnostic : notDiagnostic).ExportMinimum > LogEventLevel.Debug) raised++;
            }
        }

        Run();
        var before = GC.GetAllocatedBytesForCurrentThread();
        Run();
        var delta = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, delta);
        Assert.True(raised > 0);
    }
}
