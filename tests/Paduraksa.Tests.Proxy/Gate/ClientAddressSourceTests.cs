using System.Net;

using Microsoft.AspNetCore.Http;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Gate;

/// <summary>The gate's default client-address source: ClientInfo (or the socket) through the snapshot's trusted proxies.</summary>
public sealed class ClientAddressSourceTests
{
    private static readonly SnapshotCompilerOptions LoopbackEdge = new() { CloudflareEdgeRanges = CidrSet.Parse(["127.0.0.0/8", "::1"]) };

    private readonly TrustedProxyClientAddressSource _source = new();

    private static RuntimeSnapshot Snapshot(TrustedProxySpec proxies, SnapshotCompilerOptions? options = null) =>
        Compile(new ProxyConfigModel { Defaults = new DefaultsSpec { TrustedProxies = proxies } }, options ?? LoopbackEdge);

    private static RuntimeSnapshot CloudflareProxy => Snapshot(new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareProxy });

    [Fact]
    public void WithoutTrustedProxiesTheConnectionIsTheClient()
    {
        DefaultHttpContext context = TestContexts.Create("a.test", remote: IPAddress.Parse("::ffff:127.0.0.1"));
        context.Request.Headers[CloudflareHeaders.ConnectingIp] = "198.51.100.7";
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.8";

        ClientIpResolution resolved = _source.Resolve(context, Snapshot(TrustedProxySpec.Off));

        // The dual-mode spelling of an IPv4 peer is normalised: TCP on [::] and QUIC give the same key.
        Assert.Equal(IpKey.Parse("127.0.0.1"), resolved.Address);
        Assert.Equal(ClientIpSource.Socket, resolved.Source);
        Assert.False(resolved.PeerTrusted);
        Assert.False(resolved.CloudflareHeadersTrusted);
    }

    [Fact]
    public void TheConnectionsClientInfoWinsOverTheSocketAddress()
    {
        // A PROXY protocol header said the client is 203.0.113.9; the socket peer is the load balancer.
        DefaultHttpContext context = TestContexts.Create("a.test", remote: IPAddress.Parse("10.0.0.2"));
        context.Features.Set<IClientInfoFeature>(new ClientInfo(IpKey.Parse("203.0.113.9"), 4444, ConnectionAddressSource.ProxyProtocol, IpKey.Parse("10.0.0.2"), 5555));

        ClientIpResolution resolved = _source.Resolve(context, Snapshot(TrustedProxySpec.Off));

        Assert.Equal(IpKey.Parse("203.0.113.9"), resolved.Address);
        Assert.Equal(ClientIpSource.ProxyProtocol, resolved.Source);
    }

    [Theory]
    [InlineData("127.0.0.1", "198.51.100.7", "198.51.100.7", true)] // Cloudflare edge: header honoured
    [InlineData("::1", "2001:db8::7", "2001:db8::7", true)]
    [InlineData("192.0.2.50", "198.51.100.7", "192.0.2.50", false)] // anyone else: header ignored
    public void CloudflareProxyTrustsOnlyTheEdge(string peer, string header, string expected, bool trusted)
    {
        DefaultHttpContext context = TestContexts.Create("a.test", remote: IPAddress.Parse(peer));
        context.Request.Headers[CloudflareHeaders.ConnectingIp] = header;

        ClientIpResolution resolved = _source.Resolve(context, CloudflareProxy);

        Assert.Equal(IpKey.Parse(expected), resolved.Address);
        Assert.Equal(trusted, resolved.CloudflareHeadersTrusted);
        Assert.Equal(trusted ? ClientIpSource.CfConnectingIp : ClientIpSource.Socket, resolved.Source);
    }

    [Fact]
    public void TheBakedInEdgeRangesDoNotTrustLoopback()
    {
        DefaultHttpContext context = TestContexts.Create("a.test");
        context.Request.Headers[CloudflareHeaders.ConnectingIp] = "198.51.100.7";

        ClientIpResolution resolved = _source.Resolve(context, Snapshot(new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareProxy }, SnapshotCompilerOptions.Default));

        Assert.Equal(IpKey.Parse("127.0.0.1"), resolved.Address);
        Assert.False(resolved.PeerTrusted);
    }

    [Fact]
    public void GenericModeTakesTheRightMostUntrustedForwardedEntry()
    {
        DefaultHttpContext context = TestContexts.Create("a.test");
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.1, 203.0.113.5, 127.0.0.2";

        ClientIpResolution resolved = _source.Resolve(context, Snapshot(new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["127.0.0.0/8"] }));

        Assert.Equal(IpKey.Parse("203.0.113.5"), resolved.Address);
        Assert.True(resolved.PeerTrusted);
        Assert.False(resolved.CloudflareHeadersTrusted); // a generic proxy passes client-sent CF-* headers through
    }

    [Fact]
    public void FallbacksFromATrustedPeerAreCounted()
    {
        RuntimeSnapshot snapshot = CloudflareProxy;
        DefaultHttpContext missing = TestContexts.Create("a.test");
        DefaultHttpContext invalid = TestContexts.Create("a.test");
        invalid.Request.Headers[CloudflareHeaders.ConnectingIp] = "not-an-ip";

        Assert.Equal(ClientIpOutcome.HeaderMissing, _source.Resolve(missing, snapshot).Outcome);
        Assert.Equal(ClientIpOutcome.HeaderInvalid, _source.Resolve(invalid, snapshot).Outcome);
        Assert.Equal(ClientIpOutcome.HeaderInvalid, _source.Resolve(invalid, snapshot).Outcome);

        Assert.Equal(1, _source.HeaderMissing);
        Assert.Equal(2, _source.HeaderInvalid);
    }

    [Fact]
    public void TheTrustedSchemeIsResolvedInTheSameStep()
    {
        DefaultHttpContext context = TestContexts.Create("a.test");
        context.Request.Headers[CloudflareHeaders.ConnectingIp] = "198.51.100.7";
        context.Request.Headers[CloudflareHeaders.Visitor] = "{\"scheme\":\"https\"}";

        Assert.Equal(new RequestScheme(true, SchemeSource.CfVisitor), _source.Resolve(context, CloudflareProxy).Scheme);
        Assert.Equal(default, _source.Resolve(context, Snapshot(TrustedProxySpec.Off)).Scheme);
        Assert.Equal(0, _source.CloudflarePlainHttp);
    }

    [Fact]
    public void CloudflareOverPlainHttpForAPlainHttpVisitorIsCounted()
    {
        DefaultHttpContext flexible = TestContexts.Create("a.test");
        flexible.Request.Headers[CloudflareHeaders.Visitor] = "{\"scheme\":\"http\"}";
        DefaultHttpContext secure = TestContexts.Create("a.test", scheme: "https");
        secure.Request.Headers[CloudflareHeaders.Visitor] = "{\"scheme\":\"http\"}";

        _ = _source.Resolve(flexible, CloudflareProxy);
        _ = _source.Resolve(flexible, CloudflareProxy);
        _ = _source.Resolve(secure, CloudflareProxy); // reached us over TLS: not Flexible
        _ = _source.Resolve(flexible, Snapshot(TrustedProxySpec.Off)); // not Cloudflare at all

        Assert.Equal(2, _source.CloudflarePlainHttp);
    }

    [Fact]
    public void ResolvingAllocatesNothing()
    {
        DefaultHttpContext context = TestContexts.Create("a.test", remote: IPAddress.Parse("::ffff:127.0.0.1"));
        context.Request.Headers[CloudflareHeaders.ConnectingIp] = "2001:db8::7";
        context.Request.Headers[CloudflareHeaders.Visitor] = "{\"scheme\":\"https\"}";
        context.Features.Set<IClientInfoFeature>(ClientInfo.FromEndPoint(new IPEndPoint(IPAddress.Loopback, 1234)));
        (TrustedProxyClientAddressSource Source, DefaultHttpContext Context, RuntimeSnapshot Snapshot) run = (_source, context, CloudflareProxy);

        AllocAssert.Zero(run, static r => _ = r.Source.Resolve(r.Context, r.Snapshot), "TrustedProxyClientAddressSource.Resolve");
    }
}
