using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Paduraksa.Proxy.Net;
using Paduraksa.ProxyProtocol;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Net;

public sealed class ClientIpResolverTests
{
    private static readonly IpKey CloudflareEdge = IpKey.Parse("172.70.1.2");
    private static readonly IpKey CloudflareEdgeV6 = IpKey.Parse("2a06:98c0:3600::103");
    private static readonly IpKey Stranger = IpKey.Parse("198.51.100.23");
    private static readonly IpKey Cloudflared = IpKey.Parse("172.18.0.3");
    private static readonly IpKey InternalProxy = IpKey.Parse("10.0.0.2");

    private static readonly TrustedProxyPolicy Generic = TrustedProxyPolicy.Generic(CidrSet.Parse(["10.0.0.0/8", "fd00::/8"]));
    private static readonly TrustedProxyPolicy CfProxy = TrustedProxyPolicy.CloudflareProxy();
    private static readonly TrustedProxyPolicy CfTunnel = TrustedProxyPolicy.CloudflareTunnel(CidrSet.Parse(["172.18.0.0/16", "::1"]));

    private static HeaderDictionary Headers(params (string Name, StringValues Value)[] headers)
    {
        var dictionary = new HeaderDictionary();
        foreach (var (name, value) in headers)
        {
            dictionary[name] = value;
        }

        return dictionary;
    }

    private static ClientInfo Socket(IpKey peer) => new(peer, 1234, ConnectionAddressSource.Socket, peer, 1234);

    // ---- Off / untrusted ------------------------------------------------------------------------------------

    [Fact]
    public void PolicyOffUsesThePeerAndIgnoresHeaders()
    {
        var result = ClientIpResolver.Resolve(Socket(CloudflareEdge), Headers((CloudflareHeaders.ConnectingIp, "1.2.3.4"), ("X-Forwarded-For", "5.6.7.8")), TrustedProxyPolicy.Off);
        Assert.Equal(new ClientIpResolution(CloudflareEdge, ClientIpSource.Socket, ClientIpOutcome.PeerNotTrusted), result);
        Assert.False(result.PeerTrusted);
        Assert.False(result.IsFallback);
    }

    /// <summary>A trusted generic proxy (nginx) passes client-sent CF-* headers through, so PeerTrusted alone must
    /// never be taken as "CF-IPCountry / CF-Visitor are genuine".</summary>
    [Theory]
    [InlineData("off", "203.0.113.1", false, false)]
    [InlineData("generic", "10.0.0.2", true, false)]
    [InlineData("generic", "198.51.100.23", false, false)]
    [InlineData("cf-proxy", "172.70.1.2", true, true)]
    [InlineData("cf-proxy", "198.51.100.23", false, false)]
    [InlineData("cf-tunnel", "172.18.0.3", true, true)]
    [InlineData("cf-tunnel", "172.70.1.2", false, false)]
    public void CloudflareHeadersAreTrustedOnlyForATrustedPeerInCloudflareMode(string mode, string peer, bool peerTrusted, bool cfTrusted)
    {
        var policy = mode switch { "off" => TrustedProxyPolicy.Off, "generic" => Generic, "cf-proxy" => CfProxy, _ => CfTunnel };
        var headers = Headers(
            (CloudflareHeaders.ConnectingIp, "1.2.3.4"),
            (CloudflareHeaders.IpCountry, "NL"),
            (CloudflareHeaders.Visitor, "{\"scheme\":\"https\"}"),
            ("X-Forwarded-For", "1.2.3.4"));

        var result = ClientIpResolver.Resolve(Socket(IpKey.Parse(peer)), headers, policy);
        Assert.Equal(peerTrusted, result.PeerTrusted);
        Assert.Equal(cfTrusted, result.CloudflareHeadersTrusted);

        // Also on the fallback paths: a trusted Cloudflare peer with no usable header still has genuine CF-* headers.
        var missing = ClientIpResolver.Resolve(Socket(IpKey.Parse(peer)), Headers(), policy);
        Assert.Equal(cfTrusted, missing.CloudflareHeadersTrusted);
    }

    [Theory]
    [InlineData("generic")]
    [InlineData("cf-proxy")]
    [InlineData("cf-tunnel")]
    public void UntrustedPeerHeadersAreIgnored(string mode)
    {
        var policy = mode switch { "generic" => Generic, "cf-proxy" => CfProxy, _ => CfTunnel };
        var headers = Headers((CloudflareHeaders.ConnectingIp, "1.2.3.4"), (CloudflareHeaders.TrueClientIp, "1.2.3.4"), ("X-Forwarded-For", "1.2.3.4"));
        var result = ClientIpResolver.Resolve(Socket(Stranger), headers, policy);
        Assert.Equal(new ClientIpResolution(Stranger, ClientIpSource.Socket, ClientIpOutcome.PeerNotTrusted), result);
    }

    [Fact]
    public void ProxyProtocolClientIsFinalWhenNotATrustedProxy()
    {
        var info = new ClientInfo(Stranger, 1, ConnectionAddressSource.ProxyProtocol, IpKey.Parse("10.9.9.9"), 2);
        var result = ClientIpResolver.Resolve(info, Headers((CloudflareHeaders.ConnectingIp, "1.2.3.4")), CfProxy);
        Assert.Equal(new ClientIpResolution(Stranger, ClientIpSource.ProxyProtocol, ClientIpOutcome.PeerNotTrusted), result);
    }

    [Fact]
    public void ProxyProtocolPeerInCloudflareRangesHonoursCfConnectingIp()
    {
        // Cloudflare → L4 load balancer (PROXY protocol) → us: trust applies to the PROXY-protocol address.
        var info = new ClientInfo(CloudflareEdge, 1, ConnectionAddressSource.ProxyProtocol, IpKey.Parse("10.9.9.9"), 2);
        var result = ClientIpResolver.Resolve(info, Headers((CloudflareHeaders.ConnectingIp, "203.0.113.5")), CfProxy);
        Assert.Equal(new ClientIpResolution(IpKey.Parse("203.0.113.5"), ClientIpSource.CfConnectingIp, ClientIpOutcome.FromHeader, CloudflareHeadersTrusted: true), result);
    }

    // ---- Generic --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9")]
    [InlineData("198.51.100.1, 203.0.113.9", "203.0.113.9")]
    [InlineData("198.51.100.1, 203.0.113.9, 10.0.0.7", "203.0.113.9")]
    [InlineData("198.51.100.1,203.0.113.9,10.0.0.7,10.1.1.1", "203.0.113.9")]
    [InlineData("  203.0.113.9  ,\t10.0.0.7 ", "203.0.113.9")]
    [InlineData("10.0.0.9, 10.0.0.7", "10.0.0.9")] // all trusted → left-most
    [InlineData("2001:db8::5, fd00::1", "2001:db8::5")]
    [InlineData("::ffff:203.0.113.9", "203.0.113.9")]
    [InlineData("203.0.113.9:4711", "203.0.113.9")]
    [InlineData("[2001:db8::5]:443, 10.0.0.1", "2001:db8::5")]
    [InlineData("[2001:db8::5]", "2001:db8::5")]
    [InlineData("garbage, 203.0.113.9", "203.0.113.9")] // never looks past the right-most untrusted entry
    public void GenericTakesTheRightMostUntrustedEntry(string forwardedFor, string expected)
    {
        var result = ClientIpResolver.Resolve(Socket(InternalProxy), Headers(("X-Forwarded-For", forwardedFor)), Generic);
        Assert.Equal(new ClientIpResolution(IpKey.Parse(expected), ClientIpSource.ForwardingHeader, ClientIpOutcome.FromHeader), result);
        Assert.True(result.PeerTrusted);
    }

    [Fact]
    public void GenericMultipleHeaderLinesFormOneList()
    {
        var headers = Headers(("X-Forwarded-For", new StringValues(["198.51.100.1, 203.0.113.9", "10.0.0.7"])));
        Assert.Equal(IpKey.Parse("203.0.113.9"), ClientIpResolver.Resolve(Socket(InternalProxy), headers, Generic).Address);

        headers = Headers(("X-Forwarded-For", new StringValues(["203.0.113.9", "10.0.0.7, 10.0.0.8"])));
        Assert.Equal(IpKey.Parse("203.0.113.9"), ClientIpResolver.Resolve(Socket(InternalProxy), headers, Generic).Address);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("203.0.113.9, garbage")]
    [InlineData("garbage, 10.0.0.7")]
    [InlineData("203.0.113.9,,10.0.0.7")]
    [InlineData("203.0.113.9, ")]
    [InlineData("01.2.3.4")]
    [InlineData("1.2.3.4:99999")]
    [InlineData("1.2.3.4:")]
    [InlineData("[2001:db8::5")]
    [InlineData("[2001:db8::5]x")]
    [InlineData("[1.2.3.4]")]
    [InlineData("2001:db8::5:443:1:2:3:4:5")]
    [InlineData("for=1.2.3.4")]
    public void GenericInvalidEntryFallsBackToThePeer(string forwardedFor)
    {
        var result = ClientIpResolver.Resolve(Socket(InternalProxy), Headers(("X-Forwarded-For", forwardedFor)), Generic);
        Assert.Equal(new ClientIpResolution(InternalProxy, ClientIpSource.Socket, ClientIpOutcome.HeaderInvalid), result);
        Assert.True(result.IsFallback);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void GenericMissingHeaderFallsBackToThePeer(string? forwardedFor)
    {
        var headers = forwardedFor is null ? Headers() : Headers(("X-Forwarded-For", forwardedFor));
        var result = ClientIpResolver.Resolve(Socket(InternalProxy), headers, Generic);
        Assert.Equal(new ClientIpResolution(InternalProxy, ClientIpSource.Socket, ClientIpOutcome.HeaderMissing), result);
    }

    [Fact]
    public void GenericReadsItsCustomHeaderAndNoCloudflareHeaders()
    {
        var policy = TrustedProxyPolicy.Generic(CidrSet.Parse(["10.0.0.0/8"]), "X-Real-IP");
        var headers = Headers(("X-Real-IP", "203.0.113.9"), (CloudflareHeaders.ConnectingIp, "1.1.1.1"), ("X-Forwarded-For", "2.2.2.2"));
        Assert.Equal(IpKey.Parse("203.0.113.9"), ClientIpResolver.Resolve(Socket(InternalProxy), headers, policy).Address);

        // A generic policy never reads CF-Connecting-IP.
        var cfOnly = Headers((CloudflareHeaders.ConnectingIp, "1.1.1.1"));
        Assert.Equal(ClientIpOutcome.HeaderMissing, ClientIpResolver.Resolve(Socket(InternalProxy), cfOnly, policy).Outcome);
    }

    // ---- Cloudflare -----------------------------------------------------------------------------------------

    [Fact]
    public void CloudflareProxyReadsCfConnectingIp()
    {
        var headers = Headers((CloudflareHeaders.ConnectingIp, "2001:db8::77"), ("X-Forwarded-For", "9.9.9.9"));
        Assert.Equal(
            new ClientIpResolution(IpKey.Parse("2001:db8::77"), ClientIpSource.CfConnectingIp, ClientIpOutcome.FromHeader, CloudflareHeadersTrusted: true),
            ClientIpResolver.Resolve(Socket(CloudflareEdge), headers, CfProxy));
        Assert.Equal(IpKey.Parse("2001:db8::77"), ClientIpResolver.Resolve(Socket(CloudflareEdgeV6), headers, CfProxy).Address);
    }

    [Fact]
    public void CloudflareProxyTrustsADualModeMappedPeer()
    {
        var mapped = IpKey.FromIPAddress(System.Net.IPAddress.Parse("::ffff:172.70.1.2"));
        Assert.Equal(ClientIpOutcome.FromHeader, ClientIpResolver.Resolve(Socket(mapped), Headers((CloudflareHeaders.ConnectingIp, "1.2.3.4")), CfProxy).Outcome);
    }

    [Theory]
    [InlineData("1.2.3.4, 5.6.7.8")]
    [InlineData("1.2.3.4:80")]
    [InlineData("")]
    [InlineData("not-an-ip")]
    public void CloudflareProxyInvalidCfConnectingIpFallsBack(string value)
    {
        // Even with True-Client-IP and XFF fallback on, an invalid CF-Connecting-IP doesn't fall through.
        var policy = TrustedProxyPolicy.CloudflareProxy(useTrueClientIp: true, forwardedForFallback: true);
        var headers = Headers((CloudflareHeaders.ConnectingIp, value), (CloudflareHeaders.TrueClientIp, "9.9.9.9"), ("X-Forwarded-For", "8.8.8.8"));
        Assert.Equal(
            new ClientIpResolution(CloudflareEdge, ClientIpSource.Socket, ClientIpOutcome.HeaderInvalid, CloudflareHeadersTrusted: true),
            ClientIpResolver.Resolve(Socket(CloudflareEdge), headers, policy));
    }

    [Fact]
    public void CloudflareProxyRepeatedCfConnectingIpIsInvalid()
    {
        var headers = Headers((CloudflareHeaders.ConnectingIp, new StringValues(["1.2.3.4", "5.6.7.8"])));
        Assert.Equal(ClientIpOutcome.HeaderInvalid, ClientIpResolver.Resolve(Socket(CloudflareEdge), headers, CfProxy).Outcome);
    }

    [Fact]
    public void CloudflareProxyTrimsOptionalWhitespace() =>
        Assert.Equal(IpKey.Parse("1.2.3.4"), ClientIpResolver.Resolve(Socket(CloudflareEdge), Headers((CloudflareHeaders.ConnectingIp, " 1.2.3.4\t")), CfProxy).Address);

    [Fact]
    public void CloudflareProxyReadsTrueClientIpOnlyWhenEnabled()
    {
        var headers = Headers((CloudflareHeaders.TrueClientIp, "203.0.113.50"));
        Assert.Equal(
            new ClientIpResolution(CloudflareEdge, ClientIpSource.Socket, ClientIpOutcome.HeaderMissing, CloudflareHeadersTrusted: true),
            ClientIpResolver.Resolve(Socket(CloudflareEdge), headers, CfProxy));

        var withTrueClientIp = TrustedProxyPolicy.CloudflareProxy(useTrueClientIp: true);
        Assert.Equal(
            new ClientIpResolution(IpKey.Parse("203.0.113.50"), ClientIpSource.TrueClientIp, ClientIpOutcome.FromHeader, CloudflareHeadersTrusted: true),
            ClientIpResolver.Resolve(Socket(CloudflareEdge), headers, withTrueClientIp));

        // CF-Connecting-IP wins when both are present.
        var both = Headers((CloudflareHeaders.ConnectingIp, "1.1.1.1"), (CloudflareHeaders.TrueClientIp, "203.0.113.50"));
        Assert.Equal(ClientIpSource.CfConnectingIp, ClientIpResolver.Resolve(Socket(CloudflareEdge), both, withTrueClientIp).Source);
    }

    [Fact]
    public void CloudflareProxyFallsBackToForwardedForOnlyWhenEnabled()
    {
        var headers = Headers(("X-Forwarded-For", "203.0.113.60, 172.70.9.9"));
        Assert.Equal(ClientIpOutcome.HeaderMissing, ClientIpResolver.Resolve(Socket(CloudflareEdge), headers, CfProxy).Outcome);

        var withFallback = TrustedProxyPolicy.CloudflareProxy(forwardedForFallback: true);
        Assert.Equal(
            new ClientIpResolution(IpKey.Parse("203.0.113.60"), ClientIpSource.ForwardingHeader, ClientIpOutcome.FromHeader, CloudflareHeadersTrusted: true),
            ClientIpResolver.Resolve(Socket(CloudflareEdge), headers, withFallback));
    }

    [Fact]
    public void CloudflareProxyAcceptsAReplacementRangeList()
    {
        var replaced = TrustedProxyPolicy.CloudflareProxy(CidrSet.Parse(["192.0.2.0/24"]));
        var headers = Headers((CloudflareHeaders.ConnectingIp, "1.2.3.4"));
        Assert.Equal(ClientIpOutcome.PeerNotTrusted, ClientIpResolver.Resolve(Socket(CloudflareEdge), headers, replaced).Outcome);
        Assert.Equal(ClientIpOutcome.FromHeader, ClientIpResolver.Resolve(Socket(IpKey.Parse("192.0.2.9")), headers, replaced).Outcome);
    }

    [Fact]
    public void CloudflareTunnelTrustsCloudflaredNotTheEdge()
    {
        var headers = Headers((CloudflareHeaders.ConnectingIp, "203.0.113.70"));
        Assert.Equal(
            new ClientIpResolution(IpKey.Parse("203.0.113.70"), ClientIpSource.CfConnectingIp, ClientIpOutcome.FromHeader, CloudflareHeadersTrusted: true),
            ClientIpResolver.Resolve(Socket(Cloudflared), headers, CfTunnel));
        Assert.Equal(IpKey.Parse("203.0.113.70"), ClientIpResolver.Resolve(Socket(IpKey.Parse("::1")), headers, CfTunnel).Address);
        Assert.Equal(ClientIpOutcome.PeerNotTrusted, ClientIpResolver.Resolve(Socket(CloudflareEdge), headers, CfTunnel).Outcome);
        Assert.Equal(ClientIpOutcome.HeaderMissing, ClientIpResolver.Resolve(Socket(Cloudflared), Headers(), CfTunnel).Outcome);
    }

    // ---- Policy / data --------------------------------------------------------------------------------------

    [Fact]
    public void IsTrustedPeerDecidesTheConnectionCapExemption()
    {
        Assert.True(CfProxy.IsTrustedPeer(CloudflareEdge));
        Assert.True(CfProxy.IsTrustedPeer(CloudflareEdgeV6));
        Assert.False(CfProxy.IsTrustedPeer(Stranger));
        Assert.True(CfTunnel.IsTrustedPeer(Cloudflared));
        Assert.False(CfTunnel.IsTrustedPeer(CloudflareEdge));
        Assert.True(Generic.IsTrustedPeer(InternalProxy));
        Assert.False(TrustedProxyPolicy.Off.IsTrustedPeer(InternalProxy));
    }

    [Fact]
    public void BakedInCloudflareRangesParse()
    {
        Assert.Equal(15, CloudflareIpRanges.IPv4.Length);
        Assert.Equal(7, CloudflareIpRanges.IPv6.Length);
        Assert.True(CloudflareIpRanges.Default.Contains(IpKey.Parse("104.16.0.1")));
        Assert.True(CloudflareIpRanges.Default.Contains(IpKey.Parse("2606:4700::1111")));
        Assert.False(CloudflareIpRanges.Default.Contains(IpKey.Parse("8.8.8.8")));
        Assert.Equal(["CF-Connecting-IP", "True-Client-IP", "CF-IPCountry", "CF-Ray", "CF-Visitor"], CloudflareHeaders.StripWhenUntrusted);
    }

    [Fact]
    public void PolicyFactoriesValidate()
    {
        Assert.Throws<ArgumentNullException>(() => TrustedProxyPolicy.Generic(null!));
        Assert.Throws<ArgumentException>(() => TrustedProxyPolicy.Generic(CidrSet.Empty, " "));
        Assert.Throws<ArgumentNullException>(() => TrustedProxyPolicy.CloudflareTunnel(null!));
        Assert.Same(CloudflareIpRanges.Default, TrustedProxyPolicy.CloudflareProxy().TrustedPeers);
    }

    // ---- Allocation -----------------------------------------------------------------------------------------

    [Fact]
    public void ResolveIsAllocationFreeInEveryMode()
    {
        var generic = (Info: Socket(InternalProxy), Headers: Headers(("X-Forwarded-For", "198.51.100.1, [2001:db8::5]:443, 10.0.0.7")), Policy: Generic);
        AllocAssert.Zero(generic, static s => _ = ClientIpResolver.Resolve(s.Info, s.Headers, s.Policy), "resolve generic XFF");

        var proxy = (Info: Socket(CloudflareEdge), Headers: Headers((CloudflareHeaders.ConnectingIp, "2001:db8::77")), Policy: CfProxy);
        AllocAssert.Zero(proxy, static s => _ = ClientIpResolver.Resolve(s.Info, s.Headers, s.Policy), "resolve Cloudflare proxy");

        var tunnel = (Info: Socket(Cloudflared), Headers: Headers((CloudflareHeaders.TrueClientIp, "203.0.113.1")), Policy: TrustedProxyPolicy.CloudflareTunnel(CidrSet.Parse(["172.18.0.0/16"]), useTrueClientIp: true));
        AllocAssert.Zero(tunnel, static s => _ = ClientIpResolver.Resolve(s.Info, s.Headers, s.Policy), "resolve Cloudflare tunnel");

        var fallback = (Info: Socket(CloudflareEdge), Headers: Headers(), Policy: TrustedProxyPolicy.CloudflareProxy(useTrueClientIp: true, forwardedForFallback: true));
        AllocAssert.Zero(fallback, static s => _ = ClientIpResolver.Resolve(s.Info, s.Headers, s.Policy), "resolve fallback");

        var untrusted = (Info: Socket(Stranger), Headers: Headers((CloudflareHeaders.ConnectingIp, "1.2.3.4")), Policy: CfProxy);
        AllocAssert.Zero(untrusted, static s => _ = ClientIpResolver.Resolve(s.Info, s.Headers, s.Policy), "resolve untrusted");

        AllocAssert.Zero(CfProxy, static p => _ = p.IsTrustedPeer(CloudflareEdge), "IsTrustedPeer");
    }
}
