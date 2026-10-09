using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Pins the request-header redaction the request-logging enricher runs per request: the joined and redacted
/// values stay as they were, while a single-valued header no longer allocates and each extra header costs
/// little more than its dictionary entry.
/// </summary>
public class RequestHeaderAllocationCutTests
{
    private const string TokenPattern = "token/[a-z]+";
    private const int FewHeaders = 4;
    private const int ManyHeaders = 36;
    private const long MaxBytesPerExtraHeader = 64;

    private static readonly Action<string> IgnoreRedaction = _ => { };

    private static CompiledRedactionPatterns Patterns() =>
        new([new Regex(TokenPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))]);

    private static HashSet<string> SensitiveHeaders() => new(["Authorization"], StringComparer.OrdinalIgnoreCase);

    private static HeaderDictionary Headers(int count)
    {
        var headers = new HeaderDictionary();
        for (var i = 0; i < count; i++)
        {
            headers[$"X-Header-{i}"] = $"value-{i}";
        }
        return headers;
    }

    private static long AllocatedBytesOfSecondRun(Action run)
    {
        run();
        var before = GC.GetAllocatedBytesForCurrentThread();
        run();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void JoinHeaderValues_SingleValue_ReturnsTheSameInstance()
    {
        var value = new string('a', 8);

        var joined = RequestHeaderRedaction.JoinHeaderValues(new StringValues(value));

        Assert.Same(value, joined);
    }

    [Fact]
    public void JoinHeaderValues_SingleValue_DoesNotAllocateOnTheSecondCall()
    {
        var values = new StringValues("application/json");

        var allocated = AllocatedBytesOfSecondRun(() => RequestHeaderRedaction.JoinHeaderValues(values));

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void JoinHeaderValues_NoValues_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, RequestHeaderRedaction.JoinHeaderValues(StringValues.Empty));
    }

    [Fact]
    public void JoinHeaderValues_OnlyANullValue_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, RequestHeaderRedaction.JoinHeaderValues(new StringValues([null])));
    }

    [Fact]
    public void JoinHeaderValues_SeveralValuesWithANull_JoinsTheOthersWithCommaSpace()
    {
        Assert.Equal("a, b", RequestHeaderRedaction.JoinHeaderValues(new StringValues(["a", null, "b"])));
    }

    [Fact]
    public void RedactRequestHeaders_SensitiveHeader_IsRedactedAndNoted()
    {
        var headers = new HeaderDictionary { ["Authorization"] = "Bearer secret" };
        var noted = new List<string>();

        var redacted = RequestHeaderRedaction.RedactRequestHeaders(headers, SensitiveHeaders(), Patterns(), noted.Add);

        Assert.Equal("[REDACTED]", redacted["Authorization"]);
        Assert.Equal(["header:Authorization"], noted);
    }

    [Fact]
    public void RedactRequestHeaders_ValueMatchingAPattern_IsRedactedAndNoted()
    {
        var headers = new HeaderDictionary { ["X-Callback"] = "https://example.test/token/abc" };
        var noted = new List<string>();

        var redacted = RequestHeaderRedaction.RedactRequestHeaders(headers, SensitiveHeaders(), Patterns(), noted.Add);

        Assert.Equal("https://example.test/[REDACTED]", redacted["X-Callback"]);
        Assert.Equal(["header:X-Callback"], noted);
    }

    [Fact]
    public void RedactRequestHeaders_PlainValues_AreKeptAndNotNoted()
    {
        var headers = new HeaderDictionary { ["Accept"] = new StringValues(["text/html", "application/json"]) };
        var noted = new List<string>();

        var redacted = RequestHeaderRedaction.RedactRequestHeaders(headers, SensitiveHeaders(), Patterns(), noted.Add);

        Assert.Equal("text/html, application/json", redacted["Accept"]);
        Assert.Empty(noted);
    }

    [Fact]
    public void RedactRequestHeaders_EachExtraPlainHeader_AllocatesLessThan64Bytes()
    {
        var patterns = Patterns();
        var sensitive = SensitiveHeaders();
        var few = Headers(FewHeaders);
        var many = Headers(ManyHeaders);

        var fewBytes = AllocatedBytesOfSecondRun(() => RequestHeaderRedaction.RedactRequestHeaders(few, sensitive, patterns, IgnoreRedaction));
        var manyBytes = AllocatedBytesOfSecondRun(() => RequestHeaderRedaction.RedactRequestHeaders(many, sensitive, patterns, IgnoreRedaction));
        var bytesPerExtraHeader = (manyBytes - fewBytes) / (ManyHeaders - FewHeaders);

        Assert.True(
            bytesPerExtraHeader < MaxBytesPerExtraHeader,
            $"{bytesPerExtraHeader} B per extra header, expected under {MaxBytesPerExtraHeader} B");
    }
}
