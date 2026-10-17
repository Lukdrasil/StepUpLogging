using System.Text;
using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Reading the problem <c>code</c> out of a rejection body (issue #71): only a small
/// <c>application/problem+json</c> document is read, only its top-level string <c>code</c> counts,
/// and anything else is simply "no code" rather than a failure.
/// </summary>
public class RejectionProblemCodeTests
{
    private static string? Code(string body) => RejectionProblemCode.Parse(Encoding.UTF8.GetBytes(body));

    private static string DocumentOf(int length)
    {
        const string frame = """{"code":"sealing.unknown_key","pad":""}""";
        return $$"""{"code":"sealing.unknown_key","pad":"{{new string('a', length - frame.Length)}}"}""";
    }

    [Fact]
    public void Parse_TopLevelCode_ReturnsIt() =>
        Assert.Equal("sealing.unknown_key", Code("""{"type":"about:blank","status":422,"code":"sealing.unknown_key"}"""));

    [Fact]
    public void Parse_CodeOnlyInANestedObject_ReturnsNull() =>
        Assert.Null(Code("""{"errors":{"code":"nested"},"items":[{"code":"in-array"}]}"""));

    [Fact]
    public void Parse_NestedCodeBeforeTheTopLevelOne_ReturnsTheTopLevelCode() =>
        Assert.Equal("outer", Code("""{"detail":{"code":"inner"},"code":"outer"}"""));

    [Theory]
    [InlineData("""{"code":"sealing.unk""")]
    [InlineData("not json at all")]
    [InlineData("""["code"]""")]
    public void Parse_MalformedOrNotAnObject_ReturnsNull(string body) => Assert.Null(Code(body));

    [Fact]
    public void Parse_CodeWithALoneEscapedSurrogate_ReturnsNull() =>
        Assert.Null(Code("""{"code":"\uD800"}"""));

    [Fact]
    public void Parse_CodeWithInvalidUtf8_ReturnsNull()
    {
        byte[] body = [.. "{\"code\":\""u8, 0xFF, .. "\"}"u8];

        Assert.Null(RejectionProblemCode.Parse(body));
    }

    [Fact]
    public void Parse_EmptyBody_ReturnsNull() => Assert.Null(Code(""));

    [Fact]
    public void Parse_NumericCode_ReturnsNull() => Assert.Null(Code("""{"code":42}"""));

    [Fact]
    public void Parse_BodyOfExactlySixteenKiB_ReturnsTheCode() =>
        Assert.Equal("sealing.unknown_key", Code(DocumentOf(16 * 1024)));

    [Fact]
    public void Parse_BodyOverSixteenKiB_ReturnsNull() =>
        Assert.Null(Code(DocumentOf((16 * 1024) + 1)));

    [Fact]
    public async Task ReadAsync_NotAProblemMediaType_ReturnsNull()
    {
        using var content = new StringContent("""{"code":"sealing.unknown_key"}""", Encoding.UTF8, "application/json");

        Assert.Null(await RejectionProblemCode.ReadAsync(content, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_ProblemJsonWithCharset_ReturnsTheCode()
    {
        using var content = new StringContent("""{"code":"sealing.unknown_key"}""", Encoding.UTF8, "application/problem+json");
        Assert.Equal("application/problem+json; charset=utf-8", content.Headers.ContentType!.ToString());

        Assert.Equal("sealing.unknown_key", await RejectionProblemCode.ReadAsync(content, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_ProblemJsonInOtherCase_ReturnsTheCode()
    {
        using var content = new StringContent("""{"code":"sealing.unknown_key"}""", Encoding.UTF8, "Application/Problem+JSON");

        Assert.Equal("sealing.unknown_key", await RejectionProblemCode.ReadAsync(content, TestContext.Current.CancellationToken));
    }
}
