using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Serilog.Events;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

public class ExcludePathMatchingTests
{
    [Theory]
    [InlineData("/api", true)]
    [InlineData("/api/x", true)]
    [InlineData("/api/users/1", true)]
    [InlineData("/apifoo", false)]
    [InlineData("/apis", false)]
    [InlineData("/other", false)]
    public void IsExcludedPath_WildcardPrefix_MatchesSegmentBoundaryOnly(string path, bool expected)
    {
        Assert.Equal(expected, new ExcludedPaths(["/api/*"]).Contains(path));
    }

    [Theory]
    [InlineData("/health", true)]
    [InlineData("/healthz", false)]
    [InlineData("/health/live", false)]
    public void IsExcludedPath_ExactEntry_MatchesWholePathOnly(string path, bool expected)
    {
        Assert.Equal(expected, new ExcludedPaths(["/health"]).Contains(path));
    }

    [Fact]
    public void IsExcludedPath_MatchesRegardlessOfCase()
    {
        var excluded = new ExcludedPaths(["/health", "/api/*"]);

        Assert.True(excluded.Contains("/HEALTH"));
        Assert.True(excluded.Contains("/API/X"));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("/", "/")]
    [InlineData("/health/", "/health")]
    [InlineData("/api/x//", "/api/x")]
    public void NormalizedPath_TrimsTrailingSlashesExceptForTheRoot(string? path, string expected)
    {
        var context = new DefaultHttpContext();
        context.Features.Get<IHttpRequestFeature>()!.Path = path!;

        Assert.Equal(expected, ExcludedPaths.NormalizedPath(context.Request));
    }

    [Fact]
    public async Task AdditionalExcludePathsFromConfiguration_ExcludesTheAddedPathAndKeepsTheDefaults()
    {
        var captureKey = Guid.NewGuid().ToString("N");
        var settings = new Dictionary<string, string?>
        {
            ["SerilogStepUp:EnableOtlpExporter"] = "false",
            ["SerilogStepUp:AlwaysLogRequestSummary"] = "true",
            ["SerilogStepUp:AdditionalExcludePaths:0"] = "/.well-known/*",
            ["Serilog:Using:0"] = typeof(KeyedCaptureSink).Assembly.GetName().Name,
            ["Serilog:WriteTo:0:Name"] = nameof(KeyedCaptureSink.StepUpCapture),
            ["Serilog:WriteTo:0:Args:key"] = captureKey,
        };

        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(settings);
            builder.AddStepUpLogging();

            await using (var app = builder.Build())
            {
                app.UseStepUpRequestLogging();
                app.MapGet("/ping", () => "pong");

                await app.StartAsync(TestContext.Current.CancellationToken);
                using var client = app.GetTestClient();
                foreach (var path in new[] { "/health", "/health/", "/.well-known/ogham-sealing-keys.json", "/ping" })
                {
                    using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
                }

                await Task.Delay(200, TestContext.Current.CancellationToken);
                await app.StopAsync(TestContext.Current.CancellationToken);
            }

            var summaryPaths = KeyedCaptureSink.Events(captureKey)
                .Where(e => LogProperties.HasFlag(e, LogProperties.IsRequestSummary))
                .Select(e => (string)((ScalarValue)e.Properties["Path"]).Value!)
                .ToArray();

            Assert.Equal(["/ping"], summaryPaths);
        }
        finally
        {
            KeyedCaptureSink.Forget(captureKey);
        }
    }
}
