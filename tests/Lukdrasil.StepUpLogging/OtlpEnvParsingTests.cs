using Serilog.Sinks.OpenTelemetry;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

public class OtlpEnvParsingTests
{
    [Theory]
    [InlineData("http", OtlpProtocol.HttpProtobuf)]
    [InlineData("http/protobuf", OtlpProtocol.HttpProtobuf)]
    [InlineData("HTTP/PROTOBUF", OtlpProtocol.HttpProtobuf)]
    [InlineData("Http", OtlpProtocol.HttpProtobuf)]
    [InlineData("grpc", OtlpProtocol.Grpc)]
    [InlineData("", OtlpProtocol.Grpc)]
    [InlineData(null, OtlpProtocol.Grpc)]
    [InlineData("gopher", OtlpProtocol.Grpc)]
    public void ResolveOtlpProtocol_MapsProtocolStringToProtocol(string? protocol, OtlpProtocol expected)
    {
        Assert.Equal(expected, StepUpLoggingExtensions.ResolveOtlpProtocol(protocol));
    }

    [Theory]
    [InlineData("Authorization=Basic%20dXNlcjpwYXNz", "Authorization", "Basic dXNlcjpwYXNz")]
    [InlineData("key=a%2Cb", "key", "a,b")]
    [InlineData("x%2Dkey=value", "x-key", "value")]
    [InlineData("api-key=abc123", "api-key", "abc123")]
    public void ParseOtlpHeaders_DecodesKeyAndValue(string input, string expectedKey, string expectedValue)
    {
        var headers = StepUpLoggingExtensions.ParseOtlpHeaders(input);

        Assert.Equal(expectedValue, headers[expectedKey]);
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

    [Theory]
    [InlineData("service.namespace=my%20team", "service.namespace", "my team")]
    [InlineData("key=a%2Cb", "key", "a,b")]
    [InlineData("x%2Dkey=value", "x-key", "value")]
    [InlineData("deployment.environment=production", "deployment.environment", "production")]
    public void ParseResourceAttributes_DecodesKeyAndValue(string input, string expectedKey, string expectedValue)
    {
        var attributes = StepUpLoggingExtensions.ParseResourceAttributes(input);

        Assert.Equal(expectedValue, attributes[expectedKey]);
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
