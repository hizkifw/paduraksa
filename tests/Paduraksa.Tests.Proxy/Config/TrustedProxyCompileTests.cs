using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Net;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Config;

/// <summary>The trusted-proxy settings compile into the snapshot's <see cref="TrustedProxyPolicy"/>.</summary>
public sealed class TrustedProxyCompileTests
{
    private static ProxyConfigModel With(TrustedProxySpec spec) => new() { Defaults = new DefaultsSpec { TrustedProxies = spec } };

    [Fact]
    public void OffByDefault() => Assert.Same(TrustedProxyPolicy.Off, Compile(ProxyConfigModel.Empty).TrustedProxies);

    [Fact]
    public void GenericModeTrustsTheListedProxiesAndReadsTheHeader()
    {
        TrustedProxyPolicy policy = Compile(With(new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["10.0.0.0/8", "2001:db8::1"], Header = "X-Real-IP" })).TrustedProxies;

        Assert.Equal(TrustedProxyMode.Generic, policy.Mode);
        Assert.Equal("X-Real-IP", policy.ForwardingHeader);
        Assert.True(policy.IsTrustedPeer(IpKey.Parse("10.1.2.3")));
        Assert.True(policy.IsTrustedPeer(IpKey.Parse("2001:db8::1")));
        Assert.False(policy.IsTrustedPeer(IpKey.Parse("192.0.2.1")));
        Assert.Equal(TrustedProxyPolicy.XForwardedFor, Compile(With(new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["10.0.0.1"] })).TrustedProxies.ForwardingHeader);
        Assert.False(policy.TrustForwardedProto);
        Assert.True(Compile(With(new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["10.0.0.1"], TrustForwardedProto = true })).TrustedProxies.TrustForwardedProto);
    }

    [Fact]
    public void CloudflareProxyUsesTheEdgeRangesFromTheOptions()
    {
        TrustedProxySpec spec = new() { Mode = TrustedProxyMode.CloudflareProxy, UseTrueClientIp = true, ForwardedForFallback = true };

        TrustedProxyPolicy baked = Compile(With(spec)).TrustedProxies;
        Assert.Same(CloudflareIpRanges.Default, baked.TrustedPeers);
        Assert.True(baked.UseTrueClientIp);
        Assert.Equal(TrustedProxyPolicy.XForwardedFor, baked.ForwardingHeader);

        CidrSet refreshed = CidrSet.Parse(["127.0.0.0/8"]);
        Assert.Same(refreshed, Compile(With(spec), new SnapshotCompilerOptions { CloudflareEdgeRanges = refreshed }).TrustedProxies.TrustedPeers);
    }

    [Fact]
    public void CloudflareTunnelTrustsCloudflared()
    {
        TrustedProxyPolicy policy = Compile(With(new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareTunnel, Proxies = ["172.18.0.0/16"] })).TrustedProxies;

        Assert.Equal(TrustedProxyMode.CloudflareTunnel, policy.Mode);
        Assert.True(policy.IsTrustedPeer(IpKey.Parse("172.18.0.5")));
        Assert.Null(policy.ForwardingHeader);
    }

    public static TheoryData<TrustedProxySpec, string> Invalid => new()
    {
        { new TrustedProxySpec { Mode = TrustedProxyMode.Generic }, "needs at least one proxy" },
        { new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareTunnel }, "needs at least one proxy" },
        { new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["10.0.0.1/8"] }, "'10.0.0.1/8' isn't an IP address or CIDR" },
        { new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["proxy.local"] }, "isn't an IP address or CIDR" },
        { new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["10.0.0.1"], Header = "X Forwarded" }, "isn't a valid header name" },
        { new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareProxy, Proxies = ["10.0.0.1"] }, "list no proxies" },
        { new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareProxy, Header = "X-Real-IP" }, "only configurable in Generic mode" },
        { new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["10.0.0.1"], UseTrueClientIp = true }, "only apply to the Cloudflare modes" },
        { new TrustedProxySpec { Proxies = ["10.0.0.1"] }, "the mode is Off" },
        { new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareProxy, TrustForwardedProto = true }, "TrustForwardedProto only applies to Generic" },
        { new TrustedProxySpec { Mode = (TrustedProxyMode)42 }, "unknown mode" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void InvalidSettingsFailTheCompile(TrustedProxySpec spec, string expected)
    {
        CompileResult result = SnapshotCompiler.Compile(With(spec));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Source is TrustedProxiesErrorSource && e.Message.Contains(expected, StringComparison.Ordinal));
    }
}
