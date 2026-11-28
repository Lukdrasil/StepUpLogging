using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Pins how <see cref="RequestClient.Addresses"/> writes the client address under each <see cref="ClientIpMode"/>
/// (ADR 0042), for the connection address and for a trusted <c>X-Forwarded-For</c> entry.
/// </summary>
public class RequestClientIpModeTests
{
    private static readonly CompiledRedactionPatterns NoPatterns = new([]);

    private static HttpContext Context(string remoteIp, string? forwardedFor = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        if (forwardedFor is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        return context;
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.0/24")]
    [InlineData("2001:db8:1234:5678::1", "2001:db8:1234::/48")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.0/24")]
    [InlineData("fe80::1%3", "fe80::/48")]
    public void Prefix_MasksTheConnectionAddress(string remoteIp, string expected)
    {
        var (clientIp, _) = RequestClient.Addresses(Context(remoteIp), false, ClientIpMode.Prefix, NoPatterns);

        Assert.Equal(expected, clientIp);
    }

    [Theory]
    [InlineData("198.51.100.9", "198.51.100.0/24")]
    [InlineData("198.51.100.9:5678", "198.51.100.0/24")]
    [InlineData("[2001:db8:abcd:1::9]:443", "2001:db8:abcd::/48")]
    [InlineData("2001:db8:abcd:1::9", "2001:db8:abcd::/48")]
    public void Prefix_MasksATrustedForwardedEntry(string firstEntry, string expected)
    {
        var context = Context("10.0.0.1", $"{firstEntry}, 10.0.0.2");

        var (clientIp, _) = RequestClient.Addresses(context, true, ClientIpMode.Prefix, NoPatterns);

        Assert.Equal(expected, clientIp);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("42")]
    [InlineData("1.2")]
    [InlineData("0x7f.0.0.1")]
    [InlineData("010.0.0.1:80")]
    public void Prefix_OmitsAForwardedEntryThatIsNotAnAddress(string firstEntry)
    {
        var (clientIp, _) = RequestClient.Addresses(Context("10.0.0.1", $"{firstEntry}, 10.0.0.2"), true, ClientIpMode.Prefix, NoPatterns);

        Assert.Null(clientIp);
    }

    [Fact]
    public void Prefix_OmitsAForwardedEntryARedactionPatternChanged()
    {
        var patterns = new CompiledRedactionPatterns([new Regex(@"198\.51\.100\.\d+")]);

        var (clientIp, _) = RequestClient.Addresses(Context("10.0.0.1", "198.51.100.9"), true, ClientIpMode.Prefix, patterns);

        Assert.Null(clientIp);
    }

    [Fact]
    public void Prefix_MasksEachForwardedForEntry_AndLeavesOutEntriesThatDoNotParse()
    {
        var context = Context("10.0.0.1", "198.51.100.9, unknown, [2001:db8:abcd:1::9]:443");

        var (_, forwardedFor) = RequestClient.Addresses(context, false, ClientIpMode.Prefix, NoPatterns);

        Assert.Equal("198.51.100.0/24, 2001:db8:abcd::/48", forwardedFor);
    }

    [Fact]
    public void Prefix_OmitsForwardedFor_WhenNoEntryParses()
    {
        var (_, forwardedFor) = RequestClient.Addresses(Context("10.0.0.1", "unknown"), false, ClientIpMode.Prefix, NoPatterns);

        Assert.Null(forwardedFor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Off_OmitsBothAddresses(bool trustForwardedHeaders)
    {
        var addresses = RequestClient.Addresses(Context("203.0.113.7", "198.51.100.9"), trustForwardedHeaders, ClientIpMode.Off, NoPatterns);

        Assert.Equal((null, null), addresses);
    }

    [Fact]
    public void Full_WritesTheAddressesUnmasked()
    {
        var addresses = RequestClient.Addresses(Context("::ffff:203.0.113.7", "198.51.100.9, unknown"), false, ClientIpMode.Full, NoPatterns);

        Assert.Equal(("::ffff:203.0.113.7", "198.51.100.9, unknown"), addresses);
    }

    [Fact]
    public void Full_WritesAKnownPortWhenTheConnectionAddressIsUnknown()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemotePort = 51234;

        Assert.Equal((null, 51234), RequestClient.Remote(context, trustForwardedHeaders: true, ClientIpMode.Full));
    }

    [Fact]
    public void ClientIpMode_IsOrderedFromLeastToMostDisclosure()
    {
        Assert.Equal([ClientIpMode.Off, ClientIpMode.Prefix, ClientIpMode.Full], Enum.GetValues<ClientIpMode>().Order());
    }
}
