using System.Net;
using Microsoft.AspNetCore.Http;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Gate;

/// <summary>
/// Per-host <c>Strict-Transport-Security</c> (PLAN §14 M6): compiled once from the host's resolved settings, sent by the
/// gate on HTTPS responses whatever answers them, and never over plain HTTP, to an IP literal or to a loopback name.
/// </summary>
public sealed class HstsGateTests : IDisposable
{
    private const string OneYear = "max-age=31536000";

    private readonly SnapshotStore _store = new();
    private readonly GateMiddleware _gate;

    public HstsGateTests()
    {
        _store.Publish(Bound(Model()));
        _gate = new GateMiddleware(_store, new TrustedProxyClientAddressSource());
    }

    public void Dispose() => _store.Dispose();

    private static ProxyConfigModel Model() => new()
    {
        Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
        Hosts =
        [
            ProxyHost("app", "app.test", Route("app-root", "web")) with { Hsts = new HostHstsSpec { Enabled = true } },
            ProxyHost("full", "full.test", Route("full-root", "web")) with
            {
                Hsts = new HostHstsSpec { Enabled = true, MaxAgeSeconds = 63072000, IncludeSubDomains = true, Preload = true },
            },
            ProxyHost("short", "short.test", Route("short-root", "web")) with { Hsts = new HostHstsSpec { Enabled = true, MaxAgeSeconds = 0 } },
            ProxyHost("off", "off.test", Route("off-root", "web")),
            ProxyHost("redirect", "redirect.test", Route("redirect-root", "web")) with
            {
                ForceHttps = true,
                Hsts = new HostHstsSpec { Enabled = true },
            },
            new HostSpec
            {
                Id = TestIds.Host("old"),
                Names = ["old.test"],
                Kind = HostKind.Redirect,
                Redirect = new RedirectSpec { Target = "https://new.test", PreservePath = false },
                Hsts = new HostHstsSpec { Enabled = true, MaxAgeSeconds = 86_400 },
            },
            ProxyHost("strict", "strict.test", Route("strict-root", "web")) with
            {
                Tls = new HostTlsSpec { MinimumVersion = TlsMinimumVersion.Tls13 },
                Hsts = new HostHstsSpec { Enabled = true },
            },
            new HostSpec
            {
                Id = TestIds.Host("soon"),
                Names = ["soon.test"],
                Kind = HostKind.Respond,
                Respond = new RespondSpec { StatusCode = 503, Body = "soon" },
                Hsts = new HostHstsSpec { Enabled = true, MaxAgeSeconds = 86400 },
            },
            // The names HSTS is never sent for, configured as hosts so that the gate reaches them.
            ProxyHost("local", "localhost", Route("local-root", "web")) with { Hsts = new HostHstsSpec { Enabled = true } },
            ProxyHost("under", "app.localhost", Route("under-root", "web")) with { Hsts = new HostHstsSpec { Enabled = true } },
            ProxyHost("v4", "127.0.0.1", Route("v4-root", "web")) with { Hsts = new HostHstsSpec { Enabled = true } },
            ProxyHost("v6", "[::1]", Route("v6-root", "web")) with { Hsts = new HostHstsSpec { Enabled = true } },
            ProxyHost("public-v6", "[2001:db8::1]", Route("public-v6-root", "web")) with { Hsts = new HostHstsSpec { Enabled = true } },
        ],
        Defaults = new DefaultsSpec
        {
            TrustedProxies = new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareTunnel, Proxies = ["127.0.0.1"] },
        },
    };

    private static string? Sent(DefaultHttpContext context) =>
        context.Response.Headers.ContainsKey("Strict-Transport-Security") ? context.Response.Headers.StrictTransportSecurity.ToString() : null;

    [Fact]
    public void TheCompilerPrecomputesOneValuePerHost()
    {
        RuntimeSnapshot snapshot = _store.Current;

        Assert.Equal(OneYear, snapshot.Hosts.Find("app.test")!.Hsts.ToString());
        Assert.Equal("max-age=63072000; includeSubDomains; preload", snapshot.Hosts.Find("full.test")!.Hsts.ToString());
        Assert.Equal("max-age=0", snapshot.Hosts.Find("short.test")!.Hsts.ToString());
        Assert.Equal("max-age=86400", snapshot.Hosts.Find("soon.test")!.Hsts.ToString());
        Assert.True(snapshot.Hosts.Find("off.test")!.Hsts.Count == 0);
    }

    [Fact]
    public void TheCompilerRefusesWhatCantBeSent()
    {
        static ProxyConfigModel Invalid(HostHstsSpec hsts) => new()
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
            Hosts = [ProxyHost("app", "app.test", Route("app-root", "web")) with { Hsts = hsts }],
        };

        CompileResult tooLong = SnapshotCompiler.Compile(Invalid(new HostHstsSpec { Enabled = true, MaxAgeSeconds = 63_072_001 }));
        Assert.False(tooLong.Succeeded);
        Assert.Contains("max-age", Assert.Single(tooLong.Errors).Message, StringComparison.OrdinalIgnoreCase);

        CompileResult negative = SnapshotCompiler.Compile(Invalid(new HostHstsSpec { Enabled = true, MaxAgeSeconds = -1 }));
        Assert.Contains("max-age", Assert.Single(negative.Errors).Message, StringComparison.OrdinalIgnoreCase);

        CompileResult noSubdomains = SnapshotCompiler.Compile(Invalid(new HostHstsSpec { Enabled = true, Preload = true }));
        Assert.Contains("includeSubDomains", Assert.Single(noSubdomains.Errors).Message, StringComparison.OrdinalIgnoreCase);

        CompileResult shortPreload = SnapshotCompiler.Compile(Invalid(new HostHstsSpec { Enabled = true, Preload = true, IncludeSubDomains = true, MaxAgeSeconds = 300 }));
        Assert.Contains("a year", Assert.Single(shortPreload.Errors).Message, StringComparison.OrdinalIgnoreCase);

        // Off, the same numbers are inert: nothing is sent, so nothing is refused.
        Assert.True(SnapshotCompiler.Compile(Invalid(new HostHstsSpec { MaxAgeSeconds = -5 })).Succeeded);
    }

    [Theory]
    [InlineData("app.test", "/", "https", "HTTP/1.1", OneYear)] // forwarded
    [InlineData("app.test", "/nothing", "https", "HTTP/2", OneYear)] // the gate's own 404
    [InlineData("full.test", "/", "https", "HTTP/1.1", "max-age=63072000; includeSubDomains; preload")]
    [InlineData("short.test", "/", "https", "HTTP/1.1", "max-age=0")] // 0 tells a browser to forget the host
    [InlineData("soon.test", "/", "https", "HTTP/2", "max-age=86400")] // a Respond host's fixed answer
    [InlineData("off.test", "/", "https", "HTTP/1.1", null)] // HSTS off: nothing of ours
    [InlineData("nobody.test", "/", "https", "HTTP/1.1", null)] // the default host has no settings to speak of
    [InlineData("app.test", "/", "http", "HTTP/1.1", null)] // plain HTTP never carries it
    [InlineData("redirect.test", "/", "http", "HTTP/1.1", null)] // the plain-HTTP redirect doesn't either
    [InlineData("redirect.test", "/", "https", "HTTP/1.1", OneYear)]
    [InlineData("old.test", "/deep/path", "https", "HTTP/2", "max-age=86400")] // a Redirect host's fixed answer
    [InlineData("old.test", "/deep/path", "http", "HTTP/1.1", null)] // its plain-HTTP answer carries none
    [InlineData("localhost", "/", "https", "HTTP/1.1", null)]
    [InlineData("app.localhost", "/", "https", "HTTP/1.1", null)]
    [InlineData("127.0.0.1", "/", "https", "HTTP/1.1", null)]
    [InlineData("127.0.0.1:8443", "/", "https", "HTTP/1.1", null)]
    [InlineData("[::1]", "/", "https", "HTTP/1.1", null)]
    [InlineData("[2001:db8::1]", "/", "https", "HTTP/1.1", null)] // every IP literal, not only loopback
    public void TheGateSetsTheHostsValueOnHttpsResponses(string host, string path, string scheme, string protocol, string? expected)
    {
        DefaultHttpContext context = TestContexts.Create(host, path, scheme: scheme);
        context.Request.Protocol = protocol;

        _ = _gate.Decide(context);

        Assert.Equal(expected, Sent(context));
    }

    [Fact]
    public void AMisdirectedRequestCarriesNone()
    {
        // strict.test wants TLS 1.3; this context's connection negotiated less (no handshake feature at all), so the gate
        // answers 421 before the HSTS step runs: a browser must not be pinned on the strength of a misdirected request.
        DefaultHttpContext context = TestContexts.Create("strict.test", "/", scheme: "https");

        GateDecision decision = _gate.Decide(context);

        Assert.Equal(GateAction.Respond, decision.Action);
        Assert.Equal(421, decision.Response!.StatusCode);
        Assert.Null(Sent(context));
    }

    [Fact]
    public void ARefusalCarriesItToo()
    {
        // A policy's 403 is the host's answer as much as a forwarded response.
        ProxyConfigModel model = Model() with
        {
            Hosts = [.. Model().Hosts.Select(h => h.Id == TestIds.Host("app")
                ? h with { Policies = [new IpAllowListPolicySpec(["10.0.0.0/8"])] }
                : h)],
        };
        using SnapshotStore store = new();
        store.Publish(Bound(model));
        GateMiddleware gate = new(store, new TrustedProxyClientAddressSource());
        DefaultHttpContext context = TestContexts.Create("app.test", "/", scheme: "https", remote: IPAddress.Parse("198.51.100.1"));

        GateDecision decision = gate.Decide(context);

        Assert.Equal(GateAction.Respond, decision.Action);
        Assert.Equal(403, decision.Response!.StatusCode);
        Assert.Equal(OneYear, Sent(context));
    }

    [Theory]
    [InlineData("{\"scheme\":\"https\"}", OneYear)] // cloudflared terminated the visitor's TLS: the visitor is on HTTPS
    [InlineData("{\"scheme\":\"http\"}", null)]
    [InlineData(null, null)]
    public void TheTrustedSchemeDecides(string? visitor, string? expected)
    {
        DefaultHttpContext context = TestContexts.Create("app.test", "/", remote: IPAddress.Loopback);
        if (visitor is not null)
        {
            context.Request.Headers[CloudflareHeaders.Visitor] = visitor;
        }

        _ = _gate.Decide(context);

        Assert.Equal(expected, Sent(context));
    }

    [Fact]
    public void AnUntrustedPeerCantClaimHttpsForIt()
    {
        DefaultHttpContext context = TestContexts.Create("app.test", "/", remote: IPAddress.Parse("198.51.100.1"));
        context.Request.Headers[CloudflareHeaders.Visitor] = "{\"scheme\":\"https\"}";

        _ = _gate.Decide(context);

        Assert.Null(Sent(context));
    }

    [Fact]
    public void ApplyingItAllocatesNothing()
    {
        DefaultHttpContext context = TestContexts.Create("full.test", "/", scheme: "https");
        AllocAssert.Zero((_gate, context), static s => _ = s._gate.Decide(s.context), "gate forward with HSTS");
    }

    [Fact]
    public void ApplyingItToALoopbackNameAllocatesNothing()
    {
        DefaultHttpContext context = TestContexts.Create("app.localhost", "/", scheme: "https");
        AllocAssert.Zero((_gate, context), static s => _ = s._gate.Decide(s.context), "gate forward with HSTS refused for a loopback name");
    }
}
