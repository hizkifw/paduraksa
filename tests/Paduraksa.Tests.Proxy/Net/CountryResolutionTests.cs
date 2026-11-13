using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Paduraksa.Core.Geo;
using Paduraksa.Proxy.Net;
using Paduraksa.ProxyProtocol;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Net;

/// <summary><c>CF-IPCountry</c> counts exactly where <c>CF-Connecting-IP</c> does: a trusted peer in a Cloudflare mode.</summary>
public sealed class CountryResolutionTests
{
    private static readonly IpKey CloudflareEdge = IpKey.Parse("172.70.1.2");
    private static readonly IpKey Cloudflared = IpKey.Parse("172.18.0.3");
    private static readonly IpKey Stranger = IpKey.Parse("198.51.100.23");
    private static readonly IpKey InternalProxy = IpKey.Parse("10.0.0.2");

    private static readonly TrustedProxyPolicy Generic = TrustedProxyPolicy.Generic(CidrSet.Parse(["10.0.0.0/8"]));
    private static readonly TrustedProxyPolicy CfProxy = TrustedProxyPolicy.CloudflareProxy();
    private static readonly TrustedProxyPolicy CfTunnel = TrustedProxyPolicy.CloudflareTunnel(CidrSet.Parse(["172.18.0.0/16"]));

    private static CountryCode Resolve(IpKey peer, TrustedProxyPolicy policy, params (string Name, StringValues Value)[] headers)
    {
        var dictionary = new HeaderDictionary();
        foreach (var (name, value) in headers)
        {
            dictionary[name] = value;
        }

        return ClientIpResolver.Resolve(new ClientInfo(peer, 1234, ConnectionAddressSource.Socket, peer, 1234), dictionary, policy).Country;
    }

    [Theory]
    [InlineData("DE", "DE")]
    [InlineData("de", "DE")]
    [InlineData("nL", "NL")]
    [InlineData("T1", "T1")]
    [InlineData("XX", "XX")]
    [InlineData("", "XX")]
    [InlineData("D", "XX")]
    [InlineData("DEU", "XX")]
    [InlineData("1A", "XX")]
    [InlineData(" DE ", "DE")]
    [InlineData("DE,FR", "XX")]
    public void ATrustedCloudflarePeerSuppliesTheCountry(string value, string expected)
    {
        Assert.Equal(expected, Resolve(CloudflareEdge, CfProxy, (CloudflareHeaders.IpCountry, value)).ToString());
        Assert.Equal(expected, Resolve(Cloudflared, CfTunnel, (CloudflareHeaders.IpCountry, value)).ToString());
    }

    [Fact]
    public void TorIsKeptApartFromAnUnknownCountry()
    {
        Assert.True(Resolve(CloudflareEdge, CfProxy, (CloudflareHeaders.IpCountry, "T1")).IsTor);
        Assert.True(Resolve(CloudflareEdge, CfProxy, (CloudflareHeaders.IpCountry, "XX")).IsUnknown);
    }

    [Fact]
    public void AMissingOrRepeatedHeaderIsUnknown()
    {
        Assert.True(Resolve(CloudflareEdge, CfProxy).IsUnknown);
        Assert.True(Resolve(CloudflareEdge, CfProxy, (CloudflareHeaders.IpCountry, new StringValues(["DE", "DE"]))).IsUnknown);
    }

    [Fact]
    public void TheCountryDoesNotDependOnTheClientAddressHeader()
    {
        Assert.Equal("DE", Resolve(CloudflareEdge, CfProxy, (CloudflareHeaders.IpCountry, "DE"), (CloudflareHeaders.ConnectingIp, "not an address")).ToString());
        Assert.Equal("DE", Resolve(CloudflareEdge, CfProxy, (CloudflareHeaders.IpCountry, "DE"), (CloudflareHeaders.ConnectingIp, "203.0.113.5")).ToString());
    }

    [Fact]
    public void AnUntrustedPeersHeaderIsIgnoredWhateverThePolicy()
    {
        var forged = (CloudflareHeaders.IpCountry, (StringValues)"DE");

        Assert.True(Resolve(Stranger, CfProxy, forged).IsUnknown);
        Assert.True(Resolve(Stranger, CfTunnel, forged).IsUnknown);
        Assert.True(Resolve(CloudflareEdge, TrustedProxyPolicy.Off, forged).IsUnknown);
        Assert.True(Resolve(Stranger, Generic, forged).IsUnknown);

        // A trusted generic proxy passes client headers through, so it doesn't vouch for a Cloudflare one.
        Assert.True(Resolve(InternalProxy, Generic, forged).IsUnknown);

        // The tunnel's peer is cloudflared, not Cloudflare's edge, and the other way round.
        Assert.True(Resolve(CloudflareEdge, CfTunnel, forged).IsUnknown);
        Assert.True(Resolve(Cloudflared, CfProxy, forged).IsUnknown);
    }
}
