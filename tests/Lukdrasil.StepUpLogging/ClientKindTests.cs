namespace Lukdrasil.StepUpLogging.Tests;

public class ClientKindTests
{
    [Theory]
    [InlineData("kube-probe/1.29", "HealthProbe")]
    [InlineData("ELB-HealthChecker/2.0", "HealthProbe")]
    [InlineData("Googlebot/2.1", "Bot")]
    [InlineData("curl/8.5.0", "Tool")]
    [InlineData("PostmanRuntime/7.36", "Tool")]
    [InlineData("python-requests/2.31", "Library")]
    [InlineData("Go-http-client/1.1", "Library")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0) Chrome/120", "Browser")]
    [InlineData("MyService/1.0", "Unknown")]
    public void Classify_SampleUserAgent_MapsToItsKind(string userAgent, string expected)
    {
        Assert.Equal(expected, ClientKind.Classify(userAgent));
    }

    [Fact]
    public void Classify_BotWithMozillaPrefix_IsBot()
    {
        Assert.Equal("Bot", ClientKind.Classify("Mozilla/5.0 (compatible; bingbot/2.0)"));
    }

    [Fact]
    public void Classify_IgnoresCase()
    {
        Assert.Equal("Tool", ClientKind.Classify("CURL/8.0"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Classify_NullOrBlank_IsNone(string? userAgent)
    {
        Assert.Equal("None", ClientKind.Classify(userAgent));
    }
}
