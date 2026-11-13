using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Paduraksa.Proxy.Net;
using Paduraksa.ProxyProtocol;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Net;

/// <summary>The trusted request scheme: CF-Visitor / X-Forwarded-Proto only from trusted peers.</summary>
public sealed class ForwardedSchemeTests
{
    private static readonly TrustedProxyPolicy Tunnel = TrustedProxyPolicy.CloudflareTunnel(CidrSet.Parse(["127.0.0.0/8"]));
    private static readonly TrustedProxyPolicy Generic = TrustedProxyPolicy.Generic(CidrSet.Parse(["127.0.0.0/8"]));
    private static readonly TrustedProxyPolicy GenericTrustingProto = TrustedProxyPolicy.Generic(CidrSet.Parse(["127.0.0.0/8"]), trustForwardedProto: true);

    private static RequestScheme Resolve(TrustedProxyPolicy policy, string peer, string? visitor = null, string? proto = null)
    {
        HeaderDictionary headers = new();
        if (visitor is not null)
        {
            headers[CloudflareHeaders.Visitor] = visitor;
        }

        if (proto is not null)
        {
            headers["X-Forwarded-Proto"] = proto;
        }

        var client = ClientIpResolver.Resolve(IpKey.Parse(peer), ClientIpSource.Socket, headers, policy);
        return ForwardedScheme.Resolve(in client, headers, policy);
    }

    [Theory]
    [InlineData("{\"scheme\":\"https\"}", null, true, SchemeSource.CfVisitor)]
    [InlineData(" {\"scheme\":\"http\"} ", "https", false, SchemeSource.CfVisitor)] // CF-Visitor wins
    [InlineData("{\"scheme\": \"https\"}", "https", true, SchemeSource.ForwardedProto)] // not the exact shape: invalid
    [InlineData("{\"scheme\":\"HTTPS\"}", null, false, SchemeSource.Connection)]
    [InlineData("{\"scheme\":\"https\",\"x\":1}", "HTTP", false, SchemeSource.ForwardedProto)]
    [InlineData(null, "https", true, SchemeSource.ForwardedProto)]
    [InlineData(null, "https, http", false, SchemeSource.Connection)]
    [InlineData(null, null, false, SchemeSource.Connection)]
    public void ATrustedCloudflarePeerSetsTheScheme(string? visitor, string? proto, bool https, SchemeSource source) =>
        Assert.Equal(new RequestScheme(https, source), Resolve(Tunnel, "127.0.0.1", visitor, proto));

    [Fact]
    public void UntrustedPeersNeverSetIt()
    {
        Assert.Equal(default, Resolve(Tunnel, "192.0.2.1", "{\"scheme\":\"https\"}", "https"));
        Assert.Equal(default, Resolve(TrustedProxyPolicy.Off, "127.0.0.1", "{\"scheme\":\"https\"}", "https"));
        Assert.Equal(default, Resolve(GenericTrustingProto, "192.0.2.1", null, "https"));
    }

    [Fact]
    public void GenericProxiesSetItOnlyWhenTrustedForIt()
    {
        Assert.Equal(default, Resolve(Generic, "127.0.0.1", "{\"scheme\":\"https\"}", "https"));
        Assert.Equal(new RequestScheme(true, SchemeSource.ForwardedProto), Resolve(GenericTrustingProto, "127.0.0.1", "{\"scheme\":\"http\"}", "https"));
    }

    [Fact]
    public void TheEffectiveSchemeFallsBackToTheConnection()
    {
        Assert.True(default(RequestScheme).EffectiveIsHttps(connectionIsHttps: true));
        Assert.False(default(RequestScheme).EffectiveIsHttps(connectionIsHttps: false));
        Assert.True(new RequestScheme(true, SchemeSource.CfVisitor).EffectiveIsHttps(connectionIsHttps: false));
        Assert.False(new RequestScheme(false, SchemeSource.ForwardedProto).EffectiveIsHttps(connectionIsHttps: true));
    }

    [Fact]
    public void ParsingAllocatesNothing()
    {
        StringValues[] values = [new("{\"scheme\":\"https\"}"), new(" {\"scheme\":\"http\"}"), new("{\"scheme\":1}"), new(["a", "b"])];
        AllocAssert.Zero(
            values,
            static v =>
            {
                foreach (var value in v)
                {
                    _ = ForwardedScheme.TryParseVisitor(value, out _);
                }
            },
            "ForwardedScheme.TryParseVisitor");
    }
}
