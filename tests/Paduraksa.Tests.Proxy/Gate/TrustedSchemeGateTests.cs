using System.Net;
using Microsoft.AspNetCore.Http;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Gate;

/// <summary>The HTTP→HTTPS redirect uses the trusted request scheme, so a TLS-terminating proxy can't loop it.</summary>
public sealed class TrustedSchemeGateTests : IDisposable
{
    private readonly SnapshotStore _store = new();
    private readonly GateMiddleware _gate;

    public TrustedSchemeGateTests()
    {
        _store.Publish(Bound(new ProxyConfigModel
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
            Hosts = [ProxyHost("secure", "secure.test", Route("secure-root", "web")) with { ForceHttps = true }],
            Defaults = new DefaultsSpec { TrustedProxies = new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareTunnel, Proxies = ["127.0.0.1"] } },
        }));
        _gate = new GateMiddleware(_store, new TrustedProxyClientAddressSource());
    }

    public void Dispose() => _store.Dispose();

    [Theory]
    [InlineData("127.0.0.1", "{\"scheme\":\"https\"}", true)] // cloudflared terminated the visitor's TLS
    [InlineData("127.0.0.1", "{\"scheme\":\"http\"}", false)] // the visitor really used HTTP: redirect
    [InlineData("127.0.0.1", null, false)]
    [InlineData("192.0.2.9", "{\"scheme\":\"https\"}", false)] // not cloudflared: the header is a spoof
    public void PlainHttpBehindATrustedTerminatorIsNotRedirected(string peer, string? visitor, bool forwarded)
    {
        GateAction expected = forwarded ? GateAction.Forward : GateAction.Respond;
        DefaultHttpContext context = TestContexts.Create("secure.test", "/x", remote: IPAddress.Parse(peer));
        if (visitor is not null)
        {
            context.Request.Headers[CloudflareHeaders.Visitor] = visitor;
        }

        GateDecision decision = _gate.Decide(context);

        Assert.Equal(expected, decision.Action);
        Assert.Equal(expected == GateAction.Forward, context.Features.Get<IGateFeature>()!.State.IsHttps);
        if (expected == GateAction.Respond)
        {
            Assert.Equal(301, decision.Response!.StatusCode);
        }
    }

    [Fact]
    public void ATrustedPlainHttpVisitorOverTlsIsRedirectedToo()
    {
        // Over TLS to us, but Cloudflare says the visitor used HTTP: the visitor gets sent to HTTPS.
        DefaultHttpContext context = TestContexts.Create("secure.test", "/x", scheme: "https");
        context.Request.Headers[CloudflareHeaders.Visitor] = "{\"scheme\":\"http\"}";

        Assert.Equal(GateAction.Respond, _gate.Decide(context).Action);
    }

    [Fact]
    public void DecidingWithTheTrustedSchemeAllocatesNothing()
    {
        DefaultHttpContext context = TestContexts.Create("secure.test", "/x");
        context.Request.Headers[CloudflareHeaders.Visitor] = "{\"scheme\":\"https\"}";
        context.Request.Headers[CloudflareHeaders.ConnectingIp] = "198.51.100.7";
        AllocAssert.Zero((_gate, context), static s => _ = s._gate.Decide(s.context), "gate with a trusted CF-Visitor");
    }
}
