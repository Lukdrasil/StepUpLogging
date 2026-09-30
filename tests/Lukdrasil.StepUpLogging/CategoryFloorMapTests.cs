using System;
using System.Collections.Generic;
using System.Linq;
using Serilog.Events;

namespace Lukdrasil.StepUpLogging.Tests;

public class CategoryFloorMapTests
{
    private static CategoryFloorMap Map(params (string Key, LogEventLevel Floor)[] floors) => Map(floors, []);

    private static CategoryFloorMap Map((string Key, LogEventLevel Floor)[] floors, string[] exempt) =>
        new(floors.ToDictionary(f => f.Key, f => f.Floor, StringComparer.Ordinal), exempt);

    private static LogEventLevel? FloorOf(CategoryFloorMap map, string sourceContext, bool diagnosticActive = false) =>
        map.TryGetFloor(sourceContext, diagnosticActive, out var floor) ? floor : null;

    [Fact]
    public void NoFloors_NoCategoryGetsAFloor()
    {
        Assert.Null(FloorOf(Map(), "Test.Floored"));
    }

    [Fact]
    public void ExactKey_GetsItsFloor()
    {
        Assert.Equal(LogEventLevel.Warning, FloorOf(Map(("Test.Floored", LogEventLevel.Warning)), "Test.Floored"));
    }

    [Fact]
    public void DotChildOfKey_GetsItsFloor()
    {
        Assert.Equal(LogEventLevel.Warning, FloorOf(Map(("Test.Floored", LogEventLevel.Warning)), "Test.Floored.Child"));
    }

    [Fact]
    public void KeyWithoutDotBoundary_DoesNotMatch()
    {
        var map = Map(("Test.Floored", LogEventLevel.Warning));

        Assert.Null(FloorOf(map, "Test.FlooredX"));
        Assert.Null(FloorOf(map, "Test"));
    }

    [Fact]
    public void MatchIsOrdinalCaseSensitive()
    {
        Assert.Null(FloorOf(Map(("Test.Floored", LogEventLevel.Warning)), "test.floored"));
    }

    [Fact]
    public void FloorLevelIsReturnedAsConfigured()
    {
        var map = Map(("A", LogEventLevel.Debug), ("B", LogEventLevel.Information));

        Assert.Equal(LogEventLevel.Debug, FloorOf(map, "A.X"));
        Assert.Equal(LogEventLevel.Information, FloorOf(map, "B.X"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MostSpecificMatchingKeyDecides_WhateverTheConfiguredOrder(bool reversed)
    {
        (string, LogEventLevel)[] floors = [("Test", LogEventLevel.Warning), ("Test.Floored", LogEventLevel.Information)];
        var map = Map(reversed ? floors.Reverse().ToArray() : floors);

        Assert.Equal(LogEventLevel.Information, FloorOf(map, "Test.Floored.Child"));
        Assert.Equal(LogEventLevel.Information, FloorOf(map, "Test.Floored"));
        Assert.Equal(LogEventLevel.Warning, FloorOf(map, "Test.Other"));
        Assert.Equal(LogEventLevel.Warning, FloorOf(map, "Test"));
        Assert.Null(FloorOf(map, "Unrelated"));
    }

    [Fact]
    public void Diagnostic_LiftsANonExemptFloor()
    {
        var map = Map([("A", LogEventLevel.Warning)], []);

        Assert.Null(FloorOf(map, "A.X", diagnosticActive: true));
        Assert.Equal(LogEventLevel.Warning, FloorOf(map, "A.X", diagnosticActive: false));
    }

    [Fact]
    public void Diagnostic_KeepsAnExemptFloor_AndLiftsTheOthers()
    {
        var map = Map([("A", LogEventLevel.Warning), ("C", LogEventLevel.Information)], ["C"]);

        Assert.Equal(LogEventLevel.Information, FloorOf(map, "C.Y", diagnosticActive: true));
        Assert.Null(FloorOf(map, "A.Y", diagnosticActive: true));
    }

    [Fact]
    public void Diagnostic_ExemptNarrowerThanItsFloorKey_KeepsTheFloorOnlyUnderTheExemptPrefix()
    {
        var map = Map([("A", LogEventLevel.Warning)], ["A.B"]);

        Assert.Equal(LogEventLevel.Warning, FloorOf(map, "A.B.C", diagnosticActive: true));
        Assert.Equal(LogEventLevel.Warning, FloorOf(map, "A.B", diagnosticActive: true));
        Assert.Null(FloorOf(map, "A.X", diagnosticActive: true));
        Assert.Null(FloorOf(map, "A", diagnosticActive: true));
        Assert.Null(FloorOf(map, "A.BC", diagnosticActive: true));
    }

    [Fact]
    public void Diagnostic_ExemptSource_GetsTheFloorOfTheMostSpecificMatchingKey()
    {
        var map = Map([("A", LogEventLevel.Warning), ("A.B", LogEventLevel.Information)], ["A.B.C"]);

        Assert.Equal(LogEventLevel.Information, FloorOf(map, "A.B.C.D", diagnosticActive: true));
        Assert.Null(FloorOf(map, "A.B.X", diagnosticActive: true));
    }

    [Fact]
    public void NotDiagnostic_ExemptListDoesNotNarrowTheFloors()
    {
        var map = Map([("A", LogEventLevel.Warning)], ["A.B"]);

        Assert.Equal(LogEventLevel.Warning, FloorOf(map, "A.X", diagnosticActive: false));
        Assert.Equal(LogEventLevel.Warning, FloorOf(map, "A.B.C", diagnosticActive: false));
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new CategoryFloorMap(null!, []));
        Assert.Throws<ArgumentNullException>(() => new CategoryFloorMap(new Dictionary<string, LogEventLevel>(), null!));
    }

    [Fact]
    public void TryGetFloor_DoesNotAllocate_QS01()
    {
        var floors = Enumerable.Range(0, 20).ToDictionary(i => $"Floor{i}.Category", _ => LogEventLevel.Warning, StringComparer.Ordinal);
        var exempt = Enumerable.Range(0, 5).Select(i => $"Floor{i}.Category.Exempt").ToArray();
        var map = new CategoryFloorMap(floors, exempt);
        string[] sources = ["Floor3.Category.Exempt.Child", "Floor7.Category.Child", "Floor19.Category", "Unrelated.Category", "Floor1"];

        var matched = 0;
        void Run()
        {
            for (var i = 0; i < 10_000; i++)
            {
                if (map.TryGetFloor(sources[i % sources.Length], diagnosticActive: (i & 1) == 0, out _)) matched++;
            }
        }

        Run();
        var before = GC.GetAllocatedBytesForCurrentThread();
        Run();
        var delta = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, delta);
        Assert.True(matched > 0);
    }
}
