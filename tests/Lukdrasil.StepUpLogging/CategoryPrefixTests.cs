using System;
using Serilog.Events;
using Serilog.Parsing;

namespace Lukdrasil.StepUpLogging.Tests;

public class CategoryPrefixTests
{
    private const string EfCommandCategory = "Microsoft.EntityFrameworkCore.Database.Command";

    private static LogEvent MakeEvent(params LogEventProperty[] properties) =>
        new(DateTimeOffset.UtcNow, LogEventLevel.Information, null, new MessageTemplateParser().Parse("test"), properties);

    private static LogEventProperty SourceContext(object value) =>
        new("SourceContext", new ScalarValue(value));

    [Fact]
    public void Matches_EqualCategory_ReturnsTrue()
    {
        Assert.True(CategoryPrefix.Matches(EfCommandCategory, EfCommandCategory));
    }

    [Fact]
    public void Matches_DotChild_ReturnsTrue()
    {
        Assert.True(CategoryPrefix.Matches(EfCommandCategory + ".Internal", EfCommandCategory));
    }

    [Fact]
    public void Matches_NonDotSibling_ReturnsFalse()
    {
        Assert.False(CategoryPrefix.Matches("Microsoft.EntityFrameworkCore.Database.CommandBuilder", EfCommandCategory));
    }

    [Fact]
    public void Matches_PrefixLongerThanCategory_ReturnsFalse()
    {
        Assert.False(CategoryPrefix.Matches("Microsoft.EntityFrameworkCore", EfCommandCategory));
    }

    [Fact]
    public void Matches_IsOrdinalCaseSensitive()
    {
        Assert.False(CategoryPrefix.Matches("microsoft.entityframeworkcore.database.command", EfCommandCategory));
    }

    [Fact]
    public void TryGetSourceContext_Present_ReturnsTheValue()
    {
        Assert.True(CategoryPrefix.TryGetSourceContext(MakeEvent(SourceContext(EfCommandCategory)), out var sourceContext));
        Assert.Equal(EfCommandCategory, sourceContext);
    }

    [Fact]
    public void TryGetSourceContext_Absent_ReturnsFalse()
    {
        Assert.False(CategoryPrefix.TryGetSourceContext(MakeEvent(), out _));
    }

    [Fact]
    public void TryGetSourceContext_NonStringScalar_ReturnsFalse()
    {
        Assert.False(CategoryPrefix.TryGetSourceContext(MakeEvent(SourceContext(42)), out _));
    }

    [Fact]
    public void MatchesAny_AbsentSourceContext_ReturnsFalse()
    {
        Assert.False(CategoryPrefix.MatchesAny(MakeEvent(), [EfCommandCategory]));
    }

    [Fact]
    public void MatchesAny_EmptyPrefixes_ReturnsFalse()
    {
        Assert.False(CategoryPrefix.MatchesAny(MakeEvent(SourceContext(EfCommandCategory)), []));
    }

    [Fact]
    public void MatchesAny_AnyPrefixMatching_ReturnsTrue()
    {
        Assert.True(CategoryPrefix.MatchesAny(MakeEvent(SourceContext(EfCommandCategory + ".Internal")), ["My.App", EfCommandCategory]));
    }

    [Fact]
    public void MatchesAny_NoPrefixMatching_ReturnsFalse()
    {
        Assert.False(CategoryPrefix.MatchesAny(MakeEvent(SourceContext("My.App.Service")), [EfCommandCategory]));
    }
}
