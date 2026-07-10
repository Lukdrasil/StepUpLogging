using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

public class OtlpEnvParsingTests
{
    [Fact]
    public void ParseOtlpHeaders_PercentEncodedValue_IsDecoded()
    {
        var headers = StepUpLoggingExtensions.ParseOtlpHeaders("Authorization=Basic%20dXNlcjpwYXNz");

        Assert.Equal("Basic dXNlcjpwYXNz", headers["Authorization"]);
    }

    [Fact]
    public void ParseOtlpHeaders_EncodedCommaInValue_IsDecoded()
    {
        var headers = StepUpLoggingExtensions.ParseOtlpHeaders("key=a%2Cb");

        Assert.Equal("a,b", headers["key"]);
    }

    [Fact]
    public void ParseOtlpHeaders_PercentEncodedKey_IsDecoded()
    {
        var headers = StepUpLoggingExtensions.ParseOtlpHeaders("x%2Dkey=value");

        Assert.Equal("value", headers["x-key"]);
    }

    [Fact]
    public void ParseOtlpHeaders_UnescapedValue_IsUnchanged()
    {
        var headers = StepUpLoggingExtensions.ParseOtlpHeaders("api-key=abc123");

        Assert.Equal("abc123", headers["api-key"]);
    }

    [Fact]
    public void ParseOtlpHeaders_MalformedPairWithoutEquals_IsSkipped()
    {
        var headers = StepUpLoggingExtensions.ParseOtlpHeaders("justkey,valid=v");

        Assert.False(headers.ContainsKey("justkey"));
        Assert.Equal("v", headers["valid"]);
    }

    [Fact]
    public void ParseOtlpHeaders_EmptyKey_IsSkipped()
    {
        var headers = StepUpLoggingExtensions.ParseOtlpHeaders("=orphan,valid=v");

        Assert.Single(headers);
        Assert.Equal("v", headers["valid"]);
    }

    [Fact]
    public void ParseOtlpHeaders_MultiplePairsWithWhitespace_AreTrimmedAndDecoded()
    {
        var headers = StepUpLoggingExtensions.ParseOtlpHeaders(" a = 1%20one , b = 2%20two ");

        Assert.Equal("1 one", headers["a"]);
        Assert.Equal("2 two", headers["b"]);
    }

    [Fact]
    public void ParseResourceAttributes_PercentEncodedValue_IsDecoded()
    {
        var attributes = StepUpLoggingExtensions.ParseResourceAttributes("service.namespace=my%20team");

        Assert.Equal("my team", attributes["service.namespace"]);
    }

    [Fact]
    public void ParseResourceAttributes_EncodedCommaInValue_IsDecoded()
    {
        var attributes = StepUpLoggingExtensions.ParseResourceAttributes("key=a%2Cb");

        Assert.Equal("a,b", attributes["key"]);
    }

    [Fact]
    public void ParseResourceAttributes_PercentEncodedKey_IsDecoded()
    {
        var attributes = StepUpLoggingExtensions.ParseResourceAttributes("x%2Dkey=value");

        Assert.Equal("value", attributes["x-key"]);
    }

    [Fact]
    public void ParseResourceAttributes_UnescapedValue_IsUnchanged()
    {
        var attributes = StepUpLoggingExtensions.ParseResourceAttributes("deployment.environment=production");

        Assert.Equal("production", attributes["deployment.environment"]);
    }

    [Fact]
    public void ParseResourceAttributes_MalformedPairWithoutEquals_IsSkipped()
    {
        var attributes = StepUpLoggingExtensions.ParseResourceAttributes("justkey,valid=v");

        Assert.False(attributes.ContainsKey("justkey"));
        Assert.Equal("v", attributes["valid"]);
    }

    [Fact]
    public void ParseResourceAttributes_EmptyKey_IsSkipped()
    {
        var attributes = StepUpLoggingExtensions.ParseResourceAttributes("=orphan,valid=v");

        Assert.Single(attributes);
        Assert.Equal("v", attributes["valid"]);
    }

    [Fact]
    public void ParseResourceAttributes_MultiplePairsWithWhitespace_AreTrimmedAndDecoded()
    {
        var attributes = StepUpLoggingExtensions.ParseResourceAttributes(" a = 1%20one , b = 2%20two ");

        Assert.Equal("1 one", attributes["a"]);
        Assert.Equal("2 two", attributes["b"]);
    }
}
