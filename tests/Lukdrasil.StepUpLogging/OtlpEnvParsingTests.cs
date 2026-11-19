using Serilog.Sinks.OpenTelemetry;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

[Collection(OtlpEnvironmentCollection.Name)]
public class OtlpEnvParsingTests
{
    [Fact]
    public void ConfigureSink_ReadsProtocolHeadersAndResourceAttributesFromTheEnvironment()
    {
        string[] names = ["OTEL_EXPORTER_OTLP_PROTOCOL", "OTEL_EXPORTER_OTLP_HEADERS", "OTEL_RESOURCE_ATTRIBUTES"];
        var previous = Array.ConvertAll(names, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(names[0], "http/protobuf");
            Environment.SetEnvironmentVariable(names[1], "Authorization=Basic%20abc,x-tenant=t1");
            Environment.SetEnvironmentVariable(names[2], "service.name=orders,deployment.environment=test");
            var options = new BatchedOpenTelemetrySinkOptions();

            OtlpExport.ConfigureSink(options);

            Assert.Equal(OtlpProtocol.HttpProtobuf, options.Protocol);
            Assert.Equal("Basic abc", options.Headers["Authorization"]);
            Assert.Equal("t1", options.Headers["x-tenant"]);
            Assert.Equal("orders", options.ResourceAttributes["service.name"]);
            Assert.Equal("test", options.ResourceAttributes["deployment.environment"]);
        }
        finally
        {
            for (var i = 0; i < names.Length; i++)
            {
                Environment.SetEnvironmentVariable(names[i], previous[i]);
            }
        }
    }

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
        Assert.Equal(expected, OtlpExport.ResolveOtlpProtocol(protocol));
    }

    [Theory]
    [InlineData("Authorization=Basic%20dXNlcjpwYXNz", "Authorization", "Basic dXNlcjpwYXNz")]
    [InlineData("service.namespace=my%20team", "service.namespace", "my team")]
    [InlineData("key=a%2Cb", "key", "a,b")]
    [InlineData("x%2Dkey=value", "x-key", "value")]
    [InlineData("api-key=abc123", "api-key", "abc123")]
    public void ParseBaggageStylePairs_DecodesKeyAndValue(string input, string expectedKey, string expectedValue)
    {
        var pairs = Parse(input);

        Assert.Equal(expectedValue, pairs[expectedKey]);
    }

    [Fact]
    public void ParseBaggageStylePairs_MalformedPairWithoutEquals_IsSkipped()
    {
        var pairs = Parse("justkey,valid=v");

        Assert.False(pairs.ContainsKey("justkey"));
        Assert.Equal("v", pairs["valid"]);
    }

    [Fact]
    public void ParseBaggageStylePairs_EmptyKey_IsSkipped()
    {
        var pairs = Parse("=orphan,valid=v");

        Assert.Single(pairs);
        Assert.Equal("v", pairs["valid"]);
    }

    [Fact]
    public void ParseBaggageStylePairs_MultiplePairsWithWhitespace_AreTrimmedAndDecoded()
    {
        var pairs = Parse(" a = 1%20one , b = 2%20two ");

        Assert.Equal("1 one", pairs["a"]);
        Assert.Equal("2 two", pairs["b"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseBaggageStylePairs_BlankInput_YieldsNothing(string? input)
    {
        Assert.Empty(Parse(input));
    }

    private static Dictionary<string, string> Parse(string? input)
        => OtlpExport.ParseBaggageStylePairs(input).ToDictionary(p => p.Key, p => p.Value);
}
