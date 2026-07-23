using System.Collections.Generic;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

public class ExcludePathMatchingTests
{
    private static readonly IReadOnlySet<string> NoExact = new HashSet<string>();
    private static readonly IReadOnlyList<string> NoPrefixes = System.Array.Empty<string>();

    [Theory]
    [InlineData("/api", true)]
    [InlineData("/api/x", true)]
    [InlineData("/api/users/1", true)]
    [InlineData("/apifoo", false)]
    [InlineData("/apis", false)]
    [InlineData("/other", false)]
    public void IsExcludedPath_WildcardPrefix_MatchesSegmentBoundaryOnly(string path, bool expected)
    {
        var prefixes = new[] { "/api" };

        Assert.Equal(expected, StepUpLoggingExtensions.IsExcludedPath(path, NoExact, prefixes));
    }

    [Theory]
    [InlineData("/health", true)]
    [InlineData("/healthz", false)]
    [InlineData("/health/live", false)]
    public void IsExcludedPath_ExactEntry_MatchesWholePathOnly(string path, bool expected)
    {
        var exact = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "/health" };

        Assert.Equal(expected, StepUpLoggingExtensions.IsExcludedPath(path, exact, NoPrefixes));
    }

    [Fact]
    public void IsExcludedPath_MatchesRegardlessOfCase()
    {
        var exact = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "/health" };

        Assert.True(StepUpLoggingExtensions.IsExcludedPath("/HEALTH", exact, NoPrefixes));
        Assert.True(StepUpLoggingExtensions.IsExcludedPath("/API/X", NoExact, new[] { "/api" }));
    }
}
