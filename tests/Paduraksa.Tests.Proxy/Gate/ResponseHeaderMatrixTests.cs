using System.Diagnostics.CodeAnalysis;
using System.Net;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Http;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Proxy.Tls;
using Paduraksa.Tests.Shared;

using Yarp.ReverseProxy.Transforms;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Gate;

/// <summary>
/// The per-host response headers as one table: <c>Alt-Svc</c>, <c>Strict-Transport-Security</c>,
/// <c>Server</c> and the response <c>X-Request-Id</c> over every kind of answer, scheme, connection and exempt name. They
/// are one compiled list per host, and the gate, the reapply after a forward and YARP's response transform all apply it.
/// <para><b>The expected headers are data, not parameters.</b> A row names every header the host has an operation for: a
/// value for one the answer carries, the bare name for one it doesn't, and <c>*</c> for the request's own ID. The
/// assertion compares that whole set with the response, so a header can't be left out of a row, and a new header becomes
/// a name every row must mention rather than a new parameter. The end-to-end tests through a real host
/// (<c>PublicHstsTests</c>, <c>PublicServerHeaderTests</c>, <c>PublicHttp3Tests</c>) keep one path per header on the wire,
/// and the special paths (502, WebSocket) keep theirs.</para>
/// </summary>
public sealed class ResponseHeaderMatrixTests : IDisposable
{
    private const string OneYear = "max-age=31536000";
    private const string TwoYears = "max-age=63072000; includeSubDomains; preload";
    private const string Advertised = "h3=\":8443\"; ma=86400";

    /// <summary>The request's own ID, as a row writes it: it is materialised per request, so a row can't spell it.</summary>
    private const string Own = "*";

    /// <summary>What YARP leaves in a response header after copying the upstream's own next to ours.</summary>
    private static readonly StringValues UpstreamAndOurs = new(["Paduraksa", "upstream/1.0"]);

    private static readonly CertificateId Certificate = TestIds.Certificate("cert");

    private static readonly HostHeadersSpec Custom = new()
    {
        Response =
        [
            new HeaderRuleSpec("Server", HeaderOperation.Set, "custom/9"),
            new HeaderRuleSpec("X-Frame-Options", HeaderOperation.Set, "DENY"),
            new HeaderRuleSpec("Vary", HeaderOperation.Add, "Origin"),
            new HeaderRuleSpec("X-Powered-By", HeaderOperation.Remove, null),
        ],
    };

    private static readonly HostHeadersSpec CustomApi = new()
    {
        Response =
        [
            new HeaderRuleSpec("Server", HeaderOperation.Set, "custom/9"),
            new HeaderRuleSpec("X-Frame-Options", HeaderOperation.Remove, null),
            new HeaderRuleSpec("Vary", HeaderOperation.Add, "Origin"),
            new HeaderRuleSpec("X-Powered-By", HeaderOperation.Remove, null),
        ],
    };

    private readonly TestPki _pki = TestPki.Create();
    private readonly SnapshotStore _store = new();
    private readonly GateMiddleware _gate;

    public ResponseHeaderMatrixTests()
    {
        _store.Publish(Bound(Model(), options: Options()));
        _gate = new GateMiddleware(_store, new TrustedProxyClientAddressSource());
    }

    public void Dispose()
    {
        _store.Dispose();
        _pki.Dispose();
    }

    private SnapshotCompilerOptions Options() => new()
    {
        Certificates = new OneCertificate(_pki.Leaf("any", "*.test", "app.test")),
        PublicHttpsPort = 8443,
        AdvertiseHttp3 = true,
    };

    private static ProxyConfigModel Model() => new()
    {
        Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
        Hosts =
        [
            // Everything on: Alt-Svc, HSTS, the built-in Server, the ID to both legs.
            ProxyHost("app", "app.test", Route("app-root", "web")) with
            {
                Certificate = Certificate,
                Hsts = new HostHstsSpec { Enabled = true },
            },
            // Every header at a non-default mode.
            ProxyHost("full", "full.test", Route("full-root", "web")) with
            {
                Certificate = Certificate,
                Hsts = new HostHstsSpec { Enabled = true, MaxAgeSeconds = 63_072_000, IncludeSubDomains = true, Preload = true },
                ServerHeader = new HostServerHeaderSpec { Mode = ServerHeaderMode.Custom, Value = "edge/1.0" },
                RequestIdPropagation = RequestIdPropagation.ResponseOnly,
            },
            // The forget-the-host value: 0 is a wire value like any other.
            ProxyHost("forget", "forget.test", Route("forget-root", "web")) with
            {
                Certificate = Certificate,
                Hsts = new HostHstsSpec { Enabled = true, MaxAgeSeconds = 0 },
            },
            // Each header off in its own way.
            ProxyHost("bare", "bare.test", Route("bare-root", "web")) with
            {
                Certificate = Certificate,
                ServerHeader = new HostServerHeaderSpec { Mode = ServerHeaderMode.Upstream },
                RequestIdPropagation = RequestIdPropagation.UpstreamOnly,
            },
            ProxyHost("removed", "removed.test", Route("removed-root", "web")) with
            {
                Certificate = Certificate,
                ServerHeader = new HostServerHeaderSpec { Mode = ServerHeaderMode.Remove },
            },
            ProxyHost("noh3", "noh3.test", Route("noh3-root", "web")) with
            {
                Certificate = Certificate,
                Tls = new HostTlsSpec { Http3 = false },
            },
            // A name no certificate covers: it has no entry in the certificate table, so no Alt-Svc.
            ProxyHost("certless", "certless.test", Route("certless-root", "web")) with
            {
                Hsts = new HostHstsSpec { Enabled = true },
            },
            // No HTTPS at all: an HTTPS request for it is misdirected.
            ProxyHost("nohost", "nohttps.test", Route("nohost-root", "web")) with { Https = false },
            // A host that forces HTTPS, for the redirect it answers on plain HTTP.
            ProxyHost("secure", "secure.test", Route("secure-root", "web")) with
            {
                Certificate = Certificate,
                ForceHttps = true,
                Hsts = new HostHstsSpec { Enabled = true },
            },
            // An IP literal only: no name in the certificate table, so no Alt-Svc.
            ProxyHost("literal", "192.0.2.10", Route("literal-root", "web")) with
            {
                Certificate = Certificate,
                Names = ["192.0.2.10", "[2001:db8::10]"],
                Hsts = new HostHstsSpec { Enabled = true },
            },
            // The names HSTS never goes to, configured so the gate reaches them.
            ProxyHost("local", "localhost", Route("local-root", "web")) with { Certificate = Certificate, Hsts = new HostHstsSpec { Enabled = true } },
            ProxyHost("under", "app.localhost", Route("under-root", "web")) with { Certificate = Certificate, Hsts = new HostHstsSpec { Enabled = true } },
            ProxyHost("v4", "127.0.0.1", Route("v4-root", "web")) with { Certificate = Certificate, Hsts = new HostHstsSpec { Enabled = true } },
            ProxyHost("v6", "[::1]", Route("v6-root", "web")) with { Certificate = Certificate, Hsts = new HostHstsSpec { Enabled = true } },
            // The answers that are not a forward, and the 421.
            ProxyHost("strict", "strict.test", Route("strict-root", "web")) with
            {
                Certificate = Certificate,
                Tls = new HostTlsSpec { MinimumVersion = TlsMinimumVersion.Tls13 },
                Hsts = new HostHstsSpec { Enabled = true },
                ServerHeader = new HostServerHeaderSpec { Mode = ServerHeaderMode.Custom, Value = "strict/1.0" },
            },
            // Custom response rules: a set of Server wins over the built-in one, an add joins the upstream's value, a remove
            // takes the upstream's off; a route of its own replaces one rule by name and keeps the rest.
            ProxyHost("custom", "custom.test", Route("custom-root", "web"), Route("custom-api", "web", "/api") with { Headers = CustomApi }) with
            {
                Certificate = Certificate,
                Headers = Custom,
            },
            // Removing Server with a rule takes off the built-in one too.
            ProxyHost("unserved", "unserved.test", Route("unserved-root", "web")) with
            {
                Certificate = Certificate,
                Headers = new HostHeadersSpec { Response = [new HeaderRuleSpec("Server", HeaderOperation.Remove, null), new HeaderRuleSpec("X-Frame-Options", HeaderOperation.Set, "DENY")] },
            },
            new HostSpec
            {
                Id = TestIds.Host("old"),
                Names = ["old.test"],
                Kind = HostKind.Redirect,
                Certificate = Certificate,
                Redirect = new RedirectSpec { Target = "https://new.test", PreservePath = false },
                Hsts = new HostHstsSpec { Enabled = true, MaxAgeSeconds = 86_400 },
                ServerHeader = new HostServerHeaderSpec { Mode = ServerHeaderMode.Remove },
            },
            new HostSpec
            {
                Id = TestIds.Host("soon"),
                Names = ["soon.test"],
                Kind = HostKind.Respond,
                Certificate = Certificate,
                Respond = new RespondSpec { StatusCode = 503, Body = "soon" },
                Hsts = new HostHstsSpec { Enabled = true, MaxAgeSeconds = 86_400 },
                ServerHeader = new HostServerHeaderSpec { Mode = ServerHeaderMode.Custom, Value = "edge/2.0" },
            },
        ],
        Defaults = new DefaultsSpec
        {
            // The global defaults an unknown host follows: a custom Server, no request ID to the client, no HSTS.
            ServerHeader = new HostServerHeaderSpec { Mode = ServerHeaderMode.Custom, Value = "global/1.0" },
            RequestIdPropagation = RequestIdPropagation.UpstreamOnly,
            TrustedProxies = new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareTunnel, Proxies = ["127.0.0.1"] },
        },
    };

    /// <summary>
    /// The matrix: the whole set of headers each answer carries, over the answer kinds x schemes x connections x names.
    /// </summary>
    [Theory]
    // Forwarded: every header the host has.
    [InlineData("app.test", "/", "https", "HTTP/1.1", "Server=Paduraksa|Strict-Transport-Security=max-age=31536000|Alt-Svc=h3=\":8443\"; ma=86400|X-Request-Id=*")]
    [InlineData("full.test", "/", "https", "HTTP/2", "Server=edge/1.0|Strict-Transport-Security=max-age=63072000; includeSubDomains; preload|Alt-Svc=h3=\":8443\"; ma=86400|X-Request-Id=*")]
    // Plain HTTP: Alt-Svc advertises another transport, HSTS goes on HTTPS answers only; Server and the ID are unaffected.
    [InlineData("app.test", "/", "http", "HTTP/1.1", "Server=Paduraksa|Strict-Transport-Security|Alt-Svc|X-Request-Id=*")]
    // HTTP/3: already the transport Alt-Svc invites a client onto; HSTS still goes (it is about the scheme).
    [InlineData("app.test", "/", "https", "HTTP/3", "Server=Paduraksa|Strict-Transport-Security=max-age=31536000|Alt-Svc|X-Request-Id=*")]
    // A host that opted out of HTTP/3, and a name no certificate covers: neither advertises it.
    [InlineData("noh3.test", "/", "https", "HTTP/1.1", "Server=Paduraksa|Strict-Transport-Security|Alt-Svc|X-Request-Id=*")]
    [InlineData("certless.test", "/", "https", "HTTP/1.1", "Server=Paduraksa|Strict-Transport-Security=max-age=31536000|Alt-Svc|X-Request-Id=*")]
    // Each mode of each header:
    [InlineData("forget.test", "/", "https", "HTTP/1.1", "Server=Paduraksa|Strict-Transport-Security=max-age=0|Alt-Svc=h3=\":8443\"; ma=86400|X-Request-Id=*")]
    [InlineData("bare.test", "/", "https", "HTTP/1.1", "Server|Strict-Transport-Security|Alt-Svc=h3=\":8443\"; ma=86400|X-Request-Id")]
    [InlineData("removed.test", "/", "https", "HTTP/1.1", "Server|Strict-Transport-Security|Alt-Svc=h3=\":8443\"; ma=86400|X-Request-Id=*")]
    [InlineData("192.0.2.10", "/", "https", "HTTP/1.1", "Server=Paduraksa|Strict-Transport-Security|Alt-Svc|X-Request-Id=*")]
    // Gate answers: the host's headers hold for all of them, on HTTPS.
    [InlineData("app.test", "/nothing", "https", "HTTP/1.1", "Server=Paduraksa|Strict-Transport-Security=max-age=31536000|Alt-Svc=h3=\":8443\"; ma=86400|X-Request-Id=*")]
    [InlineData("soon.test", "/", "https", "HTTP/2", "Server=edge/2.0|Strict-Transport-Security=max-age=86400|Alt-Svc=h3=\":8443\"; ma=86400|X-Request-Id=*")]
    [InlineData("old.test", "/deep/path", "https", "HTTP/2", "Server|Strict-Transport-Security=max-age=86400|Alt-Svc=h3=\":8443\"; ma=86400|X-Request-Id=*")]
    // Custom rules ride the response transform on a forward (the gate's own pass leaves them to it) and the gate's own answer.
    [InlineData("custom.test", "/", "https", "HTTP/1.1", "Server=Paduraksa|Strict-Transport-Security|Alt-Svc=h3=\":8443\"; ma=86400|X-Request-Id=*|X-Frame-Options|Vary|X-Powered-By")]
    [InlineData("unserved.test", "/", "https", "HTTP/2", "Server=Paduraksa|Strict-Transport-Security|Alt-Svc=h3=\":8443\"; ma=86400|X-Request-Id=*|X-Frame-Options")]
    // The HTTPS redirect a force-HTTPS host answers on plain HTTP: a gate answer like any other, and neither header that
    // needs HTTPS goes on a plain-HTTP answer.
    [InlineData("secure.test", "/", "http", "HTTP/1.1", "Server=Paduraksa|Strict-Transport-Security|Alt-Svc|X-Request-Id=*")]
    [InlineData("secure.test", "/", "https", "HTTP/1.1", "Server=Paduraksa|Strict-Transport-Security=max-age=31536000|Alt-Svc=h3=\":8443\"; ma=86400|X-Request-Id=*")]
    // A host with no HTTPS answers 421 on HTTPS, like the 421 of a weaker connection.
    [InlineData("nohttps.test", "/", "https", "HTTP/1.1", "Server=Paduraksa|Strict-Transport-Security|Alt-Svc|X-Request-Id=*")]
    // An unknown host follows the global defaults: their Server, their request ID, and no Alt-Svc or HSTS.
    [InlineData("nobody.test", "/", "https", "HTTP/1.1", "Server=global/1.0|Strict-Transport-Security|Alt-Svc|X-Request-Id")]
    [InlineData("nobody.test", "/", "http", "HTTP/1.1", "Server=global/1.0|Strict-Transport-Security|Alt-Svc|X-Request-Id")]
    // HSTS never goes to an IP literal or a loopback name, whatever the host says.
    [InlineData("localhost", "/", "https", "HTTP/1.1", "Server=Paduraksa|Strict-Transport-Security|Alt-Svc=h3=\":8443\"; ma=86400|X-Request-Id=*")]
    [InlineData("app.localhost", "/", "https", "HTTP/1.1", "Server=Paduraksa|Strict-Transport-Security|Alt-Svc=h3=\":8443\"; ma=86400|X-Request-Id=*")]
    [InlineData("127.0.0.1", "/", "https", "HTTP/1.1", "Server=Paduraksa|Strict-Transport-Security|Alt-Svc|X-Request-Id=*")]
    [InlineData("127.0.0.1:8443", "/", "https", "HTTP/1.1", "Server=Paduraksa|Strict-Transport-Security|Alt-Svc|X-Request-Id=*")]
    [InlineData("[::1]", "/", "https", "HTTP/1.1", "Server=Paduraksa|Strict-Transport-Security|Alt-Svc|X-Request-Id=*")]
    [InlineData("[2001:db8::10]", "/", "https", "HTTP/1.1", "Server=Paduraksa|Strict-Transport-Security|Alt-Svc|X-Request-Id=*")]
    public async Task EveryAnswerCarriesExactlyTheSetTheHostCompiled(
        string host, string path, string scheme, string protocol, string expected)
    {
        var context = TestContexts.Create(host, path, scheme: scheme);
        context.Request.Protocol = protocol;

        await _gate.InvokeAsync(context, static _ => Task.CompletedTask);

        AssertSet(expected, context);
    }

    /// <summary>
    /// A response rule can't weaken a page the proxy writes itself: the rules go on first and the page's own security headers after
    /// them, so a rule that removes <c>Content-Security-Policy</c> leaves the page's in place. A <c>Set-Cookie</c> rule, which no
    /// page sets, reaches the client like any other header the operator chose.
    /// </summary>
    [Theory]
    [InlineData("/nothing", 404, "Checking your browser")]
    [InlineData("/pow", 200, "Checking your browser")]
    public async Task ARuleCantTakeAPagesOwnSecurityHeadersOff(string path, int status, string powHeading)
    {
        var runtime = new ProofOfWorkRuntime();
        runtime.UseKey(new TestMacKey());
        HostHeadersSpec rules = new()
        {
            Response =
            [
                new HeaderRuleSpec("Content-Security-Policy", HeaderOperation.Remove, null),
                new HeaderRuleSpec("Set-Cookie", HeaderOperation.Add, "operator=1; Path=/"),
            ],
        };
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
            Hosts =
            [
                ProxyHost("pages", "pages.test", Route("pages-api", "web", "/api"), Route("pages-pow", "web", "/pow") with
                {
                    ProofOfWork = new HostProofOfWorkSpec { Enabled = true, Difficulty = 8 },
                    ProofOfWorkRouteOverride = true,
                }) with { Headers = rules, ProofOfWork = new HostProofOfWorkSpec { Difficulty = 8 } },
            ],
        };
        using SnapshotStore store = new();
        store.Publish(Bound(model, options: new SnapshotCompilerOptions { ProofOfWork = runtime }));
        GateMiddleware gate = new(store, new TrustedProxyClientAddressSource(), proofOfWork: runtime);
        var context = TestContexts.Create("pages.test", path, scheme: "http");
        context.Request.Headers.Accept = "text/html";
        context.Response.Body = new MemoryStream();

        await gate.InvokeAsync(context, static _ => Task.CompletedTask);

        Assert.Equal(status, context.Response.StatusCode);
        Assert.False(string.IsNullOrEmpty(context.Response.Headers.ContentSecurityPolicy.ToString()), "the page keeps its own Content-Security-Policy");
        Assert.Equal("operator=1; Path=/", context.Response.Headers.SetCookie.ToString());
        var body = System.Text.Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        Assert.Equal(path == "/pow", body.Contains(powHeading, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANotFoundCarriesTheHostsCustomRules()
    {
        // The gate's own answer, before any route is known: the host's rules, after its built-in headers.
        var context = TestContexts.Create("custom.test", "/", scheme: "http");
        var model = Model() with { Hosts = [.. Model().Hosts.Select(h => h.Id == TestIds.Host("custom") ? h with { Routes = [Route("custom-api", "web", "/api")] } : h)] };
        using SnapshotStore store = new();
        store.Publish(Bound(model, options: Options()));
        GateMiddleware gate = new(store, new TrustedProxyClientAddressSource());

        await gate.InvokeAsync(context, static _ => Task.CompletedTask);

        Assert.Equal(404, context.Response.StatusCode);
        AssertSet("Server=custom/9|Strict-Transport-Security|Alt-Svc|X-Request-Id=*|X-Frame-Options=DENY|Vary=Origin|X-Powered-By", context);
    }

    [Fact]
    public async Task AMisdirectedRequestCarriesOnlyTheServerHeader()
    {
        // strict.test wants TLS 1.3, and this connection negotiated less: the 421 is answered before the headers that
        // follow the TLS check, so a browser is never pinned on the strength of a misdirected request.
        var context = TestContexts.Create("strict.test", "/", scheme: "https");

        var decision = _gate.Decide(context);

        Assert.Equal((GateAction.Respond, 421), (decision.Action, decision.Response!.StatusCode));
        AssertSet("Server=strict/1.0|Strict-Transport-Security|Alt-Svc|X-Request-Id", context);
    }

    [Fact]
    public async Task AForceHttpsHostsRedirectCarriesOnlyWhatMayGoOnPlainHttp()
    {
        // The redirect is a gate answer, so the Server header and the request ID are on it; HSTS and Alt-Svc need HTTPS.
        var context = TestContexts.Create("secure.test", "/deep/path?q=1", scheme: "http");

        await _gate.InvokeAsync(context, static _ => Task.CompletedTask);

        Assert.Equal(301, context.Response.StatusCode);
        AssertSet("Server=Paduraksa|Strict-Transport-Security|Alt-Svc|X-Request-Id=*", context);
    }

    [Theory]
    [InlineData("{\"scheme\":\"https\"}", "max-age=31536000")] // cloudflared terminated the visitor's TLS: the visitor is on HTTPS
    [InlineData("{\"scheme\":\"http\"}", null)]
    [InlineData(null, null)]
    public void HstsFollowsTheTrustedScheme(string? visitor, string? expected)
    {
        // The connection is plain HTTP; the trusted proxy says what the client used.
        var context = TestContexts.Create("app.test", "/", remote: IPAddress.Loopback);
        if (visitor is not null)
        {
            context.Request.Headers[CloudflareHeaders.Visitor] = visitor;
        }

        _ = _gate.Decide(context);

        Assert.Equal(expected, Header(context, HstsHeader.HeaderName));
    }

    [Fact]
    public async Task AnUntrustedPeerCantClaimHttpsForHsts()
    {
        var context = TestContexts.Create("app.test", "/", remote: IPAddress.Parse("198.51.100.1"));
        context.Request.Headers[CloudflareHeaders.Visitor] = "{\"scheme\":\"https\"}";

        await _gate.InvokeAsync(context, static _ => Task.CompletedTask);

        AssertSet("Server=Paduraksa|Strict-Transport-Security|Alt-Svc|X-Request-Id=*", context);
    }

    private static readonly AccessListSpec Private = TestAccessLists.Allow("private", "10.0.0.0/8");

    [Fact]
    public async Task ARefusalCarriesTheHeadersToo()
    {
        // A policy's 403 is the host's answer as much as a forwarded response.
        var model = Model() with
        {
            AccessLists = [Private],
            Hosts =
            [
                .. Model().Hosts.Select(h => h.Id == TestIds.Host("app")
                    ? h with { Policies = [Private.Policy()] }
                    : h),
            ],
        };
        using SnapshotStore store = new();
        store.Publish(Bound(model, options: Options()));
        GateMiddleware gate = new(store, new TrustedProxyClientAddressSource());
        var context = TestContexts.Create("app.test", "/", scheme: "https", remote: IPAddress.Parse("198.51.100.1"));

        await gate.InvokeAsync(context, static _ => Task.CompletedTask);

        Assert.Equal(403, context.Response.StatusCode);
        AssertSet("Server=Paduraksa|Strict-Transport-Security=max-age=31536000|Alt-Svc=h3=\":8443\"; ma=86400|X-Request-Id=*", context);
    }

    /// <summary>The compiled list: one precomputed value (or absence) per header, per host, as the same kind of set.</summary>
    [Theory]
    [InlineData("full.test", "Server=edge/1.0|Strict-Transport-Security=max-age=63072000; includeSubDomains; preload|Alt-Svc=h3=\":8443\"; ma=86400|X-Request-Id=*")]
    [InlineData("bare.test", "Server|Strict-Transport-Security|Alt-Svc=h3=\":8443\"; ma=86400|X-Request-Id")]
    [InlineData("removed.test", "Server|Strict-Transport-Security|Alt-Svc=h3=\":8443\"; ma=86400|X-Request-Id=*")]
    [InlineData("noh3.test", "Server=Paduraksa|Strict-Transport-Security|Alt-Svc|X-Request-Id=*")]
    [InlineData("certless.test", "Server=Paduraksa|Strict-Transport-Security=max-age=31536000|Alt-Svc|X-Request-Id=*")]
    [InlineData("nohttps.test", "Server=Paduraksa|Strict-Transport-Security|Alt-Svc|X-Request-Id=*")]
    [InlineData("192.0.2.10", "Server=Paduraksa|Strict-Transport-Security=max-age=31536000|Alt-Svc|X-Request-Id=*")]
    public void TheCompilerPrecomputesOneListPerHost(string name, string expected)
    {
        Assert.Equal(Sorted(expected), Sorted(SetOf(_store.Current.Hosts.Find(name)!.ResponseHeaders)));

        // The global defaults a name no host serves follows: their Server and request ID, and neither of the two the
        // proxy doesn't send for a name it doesn't serve.
        Assert.Equal(
            Sorted("Server=global/1.0|Strict-Transport-Security|Alt-Svc|X-Request-Id"),
            Sorted(SetOf(_store.Current.DefaultResponseHeaders)));
    }

    [Fact]
    public void WithoutAnHttp3ListenerNobodyAdvertisesIt()
    {
        // The transport itself is missing, so the value is empty for every host: no Alt-Svc anywhere.
        var snapshot = Compile(Model(), Options() with { AdvertiseHttp3 = false });

        Assert.Equal(
            Sorted("Server=Paduraksa|Strict-Transport-Security=max-age=31536000|Alt-Svc|X-Request-Id=*"),
            Sorted(SetOf(snapshot.Hosts.Find("app.test")!.ResponseHeaders)));
    }

    [Fact]
    public void TheAltSvcValueRefusesWhatItCannotAdvertise()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AltSvcHeader(0, http3Listening: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AltSvcHeader(65_536, http3Listening: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AltSvcHeader(443, http3Listening: true, maxAgeSeconds: -1));

        // Without an HTTP/3 listener the value is empty whatever the port.
        Assert.True(new AltSvcHeader(443, http3Listening: false).Value.Count == 0);
        Assert.Equal("h3=\":8443\"; ma=86400", new AltSvcHeader(8443, http3Listening: true).Value.ToString());
    }

    /// <summary>After YARP copies an upstream's response, each mode holds: ours replaces, or the header is removed.</summary>
    [Theory]
    [InlineData("app.test", "Server=Paduraksa|Strict-Transport-Security=max-age=31536000|Alt-Svc=h3=\":8443\"; ma=86400|X-Request-Id=*")]
    [InlineData("full.test", "Server=edge/1.0|Strict-Transport-Security=max-age=63072000; includeSubDomains; preload|Alt-Svc=h3=\":8443\"; ma=86400|X-Request-Id=*")]
    // The upstream's own Server and HSTS pass through where the host has none of its own.
    [InlineData("bare.test", "Server=upstream/1.0|Strict-Transport-Security=max-age=1|Alt-Svc=h3=\":8443\"; ma=86400|X-Request-Id")]
    [InlineData("removed.test", "Server|Strict-Transport-Security=max-age=1|Alt-Svc=h3=\":8443\"; ma=86400|X-Request-Id=*")]
    // Custom rules after the copy: Server set over the upstream's and ours, an add joins the upstream's Vary, a remove drops
    // its X-Powered-By; the API route replaces X-Frame-Options with a remove and keeps the others.
    [InlineData("custom.test", "Server=custom/9|Strict-Transport-Security=max-age=1|Alt-Svc=h3=\":8443\"; ma=86400|X-Request-Id=*|X-Frame-Options=DENY|Vary=Accept-Encoding,Origin|X-Powered-By")]
    [InlineData("custom.test/api", "Server=custom/9|Strict-Transport-Security=max-age=1|Alt-Svc=h3=\":8443\"; ma=86400|X-Request-Id=*|X-Frame-Options|Vary=Accept-Encoding,Origin|X-Powered-By")]
    [InlineData("unserved.test", "Server|Strict-Transport-Security=max-age=1|Alt-Svc=h3=\":8443\"; ma=86400|X-Request-Id=*|X-Frame-Options=DENY")]
    public async Task TheResponseTransformMakesEachModeHoldAfterYarpsCopy(string hostName, string expected)
    {
        var routePath = hostName.Contains('/', StringComparison.Ordinal) ? hostName[hostName.IndexOf('/', StringComparison.Ordinal)..] : null;
        hostName = routePath is null ? hostName : hostName[..hostName.IndexOf('/', StringComparison.Ordinal)];
        var context = TestContexts.Create(hostName, "/", scheme: "https");
        context.Response.Headers["X-Powered-By"] = "Express";
        context.Response.Headers["Vary"] = "Accept-Encoding";
        context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        // What YARP copies: the upstream's own next to ours, or on its own when the host leaves it there.
        context.Response.Headers[ServerHeader.HeaderName] = KindOf(hostName, ServerHeader.HeaderName) == ResponseHeaderKind.LeaveToUpstream
            ? new StringValues("upstream/1.0")
            : UpstreamAndOurs;
        context.Response.Headers[HstsHeader.HeaderName] = "max-age=1";
        context.Response.Headers[RequestIds.HeaderName] = "upstream-id";

        await ResponseHeadersTransform.ApplyResponseAsync(Transform(context, hostName, headersCopied: true, route: routePath));

        AssertSet(expected, context);
    }

    [Fact]
    public async Task TheResponseTransformSettlesAFailedForwardAndHeadersNotCopied()
    {
        // A connection failure has no proxy response at all: the gate's own headers are what the client gets.
        var failed = TestContexts.Create("full.test", "/", scheme: "https");
        failed.Response.Headers[ServerHeader.HeaderName] = "edge/1.0";
        await ResponseHeadersTransform.ApplyResponseAsync(Transform(failed, "full.test", headersCopied: true, proxy: null));
        Assert.Equal("edge/1.0", Header(failed, ServerHeader.HeaderName));

        // With copying off the transform owns the proxy response: a removed header comes off it, and ours is not added a
        // second time next to the value the gate already set.
        var removed = TestContexts.Create("removed.test", "/", scheme: "https");
        removed.Response.Headers[ServerHeader.HeaderName] = "upstream/1.0";
        using HttpResponseMessage proxy = new();
        proxy.Headers.TryAddWithoutValidation(ServerHeader.HeaderName, "upstream/1.0");
        await ResponseHeadersTransform.ApplyResponseAsync(Transform(removed, "removed.test", headersCopied: false, proxy));
        Assert.Null(Header(removed, ServerHeader.HeaderName));
        Assert.False(proxy.Headers.Contains(ServerHeader.HeaderName));

        var ours = TestContexts.Create("app.test", "/", scheme: "https");
        ours.Response.Headers[ServerHeader.HeaderName] = "Paduraksa";
        using HttpResponseMessage upstream = new();
        upstream.Headers.TryAddWithoutValidation(ServerHeader.HeaderName, "upstream/1.0");
        await ResponseHeadersTransform.ApplyResponseAsync(Transform(ours, "app.test", headersCopied: false, upstream));
        Assert.Equal("Paduraksa", Header(ours, ServerHeader.HeaderName));
        Assert.False(upstream.Headers.Contains(ServerHeader.HeaderName));
    }

    [Fact]
    public async Task CustomRulesSettleAnUncopiedUpstreamResponseInTheRightStore()
    {
        // With copying off a transform owns the proxy response: a set or a remove takes the upstream's copy off whichever store holds
        // it (a content header lives on the content, and the other store throws for it), so the copy can't put it back.
        var context = TestContexts.Create("custom.test", "/", scheme: "https");
        using HttpResponseMessage upstream = new() { Content = new StringContent("x") };
        upstream.Headers.TryAddWithoutValidation("X-Powered-By", "Express");
        upstream.Headers.TryAddWithoutValidation("X-Frame-Options", "SAMEORIGIN");
        upstream.Content.Headers.ContentLanguage.Add("de");
        var transform = Transform(context, "custom.test", headersCopied: false, upstream);
        GateRequestState state = (GateRequestState)context.Features.Get<IGateFeature>()!;
        state.Route = new CompiledRoute
        {
            Id = TestIds.Route("lang"),
            MatchKind = RouteMatchKind.Prefix,
            Path = "/",
            Slot = 0,
            Policies = [],
            Headers = CompiledHeaders.Compile(
                new HostHeadersSpec
                {
                    Response =
                    [
                        new HeaderRuleSpec("Content-Language", HeaderOperation.Set, "en"),
                        new HeaderRuleSpec("X-Powered-By", HeaderOperation.Remove, null),
                        new HeaderRuleSpec("X-Frame-Options", HeaderOperation.Set, "DENY"),
                    ],
                },
                static message => throw new InvalidOperationException(message)),
        };

        await ResponseHeadersTransform.ApplyResponseAsync(transform);

        Assert.Empty(upstream.Content.Headers.ContentLanguage);
        Assert.False(upstream.Headers.Contains("X-Powered-By"));
        Assert.False(upstream.Headers.Contains("X-Frame-Options"));
        Assert.Equal("en", Header(context, "Content-Language"));
        Assert.Equal("DENY", Header(context, "X-Frame-Options"));
    }

    /// <summary>What a refused value does: the compiler refuses it, so nothing that can't be sent is ever sent.</summary>
    [Fact]
    public void TheCompilerRefusesValuesThatCantBeSent()
    {
        static ProxyConfigModel ModelWith(HostSettingsParts parts) => new()
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
            Hosts =
            [
                ProxyHost("app", "app.test", Route("app-root", "web")) with
                {
                    Hsts = parts.Hsts ?? HostHstsSpec.Off,
                    ServerHeader = parts.Server ?? HostServerHeaderSpec.Default,
                },
            ],
        };

        foreach (var invalid in (HostSettingsParts[])
        [
            new() { Hsts = new HostHstsSpec { Enabled = true, MaxAgeSeconds = 63_072_001 } },
            new() { Hsts = new HostHstsSpec { Enabled = true, MaxAgeSeconds = -1 } },
            new() { Hsts = new HostHstsSpec { Enabled = true, Preload = true } },
            new() { Hsts = new HostHstsSpec { Enabled = true, Preload = true, IncludeSubDomains = true, MaxAgeSeconds = 300 } },
            new() { Server = new HostServerHeaderSpec { Mode = ServerHeaderMode.Custom, Value = null } },
            new() { Server = new HostServerHeaderSpec { Mode = ServerHeaderMode.Custom, Value = string.Empty } },
            new() { Server = new HostServerHeaderSpec { Mode = ServerHeaderMode.Custom, Value = " edge/1.0" } },
            new() { Server = new HostServerHeaderSpec { Mode = ServerHeaderMode.Custom, Value = "edge/1.0 " } },
            new() { Server = new HostServerHeaderSpec { Mode = ServerHeaderMode.Custom, Value = "edge\t1.0" } },
            new() { Server = new HostServerHeaderSpec { Mode = ServerHeaderMode.Custom, Value = new string('a', 129) } },
            new() { Server = new HostServerHeaderSpec { Mode = (ServerHeaderMode)9 } },
        ])
        {
            var result = SnapshotCompiler.Compile(ModelWith(invalid));
            Assert.False(result.Succeeded);
            Assert.NotEmpty(result.Errors);
        }

        // A year of max-age with both flags, 128 characters of value, and a value no mode uses: all fine.
        foreach (var valid in (HostSettingsParts[])
        [
            new() { Hsts = new HostHstsSpec { Enabled = true, Preload = true, IncludeSubDomains = true } },
            new() { Hsts = new HostHstsSpec { Enabled = true, MaxAgeSeconds = 0 } },
            new() { Hsts = new HostHstsSpec { MaxAgeSeconds = -5 } }, // off: the numbers are inert
            new() { Server = new HostServerHeaderSpec { Mode = ServerHeaderMode.Custom, Value = new string('a', 128) } },
            new() { Server = new HostServerHeaderSpec { Mode = ServerHeaderMode.Custom, Value = "edge proxy/1.0" } },
            new() { Server = new HostServerHeaderSpec { Mode = ServerHeaderMode.Paduraksa, Value = "bad\tvalue" } },
        ])
        {
            Assert.True(SnapshotCompiler.Compile(ModelWith(valid)).Succeeded);
        }
    }

    [Theory]
    [InlineData("Paduraksa", true)]
    [InlineData("edge proxy/1.0", true)]
    [InlineData("~", true)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData(" edge/1.0", false)]
    [InlineData("edge/1.0 ", false)]
    [InlineData("edge\t1.0", false)]
    [InlineData("edge\u007f1.0", false)]
    [InlineData("edge\u00e91.0", false)]
    [InlineData("edge\r\nInjected: yes", false)]
    public void TheCustomServerValueGrammarIsTheSimpleVisibleSubset(string value, bool valid) =>
        Assert.Equal(valid, ServerHeader.IsValidValue(value));

    [Fact]
    public void ApplyingTheFullListAllocatesNothing()
    {
        // Every header at once, forwarded and answered; then a loopback name (HSTS refused), and each mode that leaves a
        // header off.
        (string Host, string Scheme)[] cases =
        [
            ("full.test", "https"),
            ("nobody.test", "https"),
            ("app.localhost", "https"),
            ("bare.test", "https"),
            ("removed.test", "https"),
        ];
        foreach (var (host, scheme) in cases)
        {
            var context = TestContexts.Create(host, "/", scheme: scheme, remote: IPAddress.Loopback);
            var gate = _gate;
            AllocAssert.Zero((gate, context), static s => _ = s.gate.Decide(s.context), $"gate with response headers for {host} over {scheme}");
        }
    }

    [Fact]
    public void TheResponseTransformAllocatesNothing()
    {
        // unserved.test: custom rules that set and remove, which is a store and a removal of precomputed values.
        foreach (var hostName in new[] { "full.test", "bare.test", "removed.test", "unserved.test" })
        {
            var context = TestContexts.Create(hostName, "/", scheme: "https");
            context.Response.Headers[ServerHeader.HeaderName] = KindOf(hostName, ServerHeader.HeaderName) == ResponseHeaderKind.LeaveToUpstream
                ? new StringValues("upstream/1.0")
                : UpstreamAndOurs;
            var transform = Transform(context, hostName, headersCopied: true);

            AllocAssert.Zero(
                transform,
                static t =>
                {
                    var task = ResponseHeadersTransform.ApplyResponseAsync(t);
                    if (!task.IsCompletedSuccessfully)
                    {
                        throw new InvalidOperationException("the response transform should complete synchronously");
                    }

                    task.GetAwaiter().GetResult();
                },
                $"response transform for {hostName}");
        }
    }

    /// <summary>
    /// The set a row expects against the response: every header the host has an operation for, with its value, or absent.
    /// <c>*</c> stands for the request's own ID.
    /// </summary>
    private static void AssertSet(string expected, DefaultHttpContext context) => Assert.Equal(Sorted(expected), Sorted(SetOf(context)));

    /// <summary>A set string in one order, so a row reads in any order: the set is what matters, not the order applied.</summary>
    private static string Sorted(string set) =>
        string.Join('|', set.Split('|').Order(StringComparer.Ordinal));

    /// <summary>The response's headers as a row writes them, in the compiled list's order.</summary>
    private static string SetOf(DefaultHttpContext context)
    {
        var state = context.Features.Get<IGateFeature>()?.State;
        var headers = state?.Host?.ResponseHeaders ?? state?.Snapshot?.DefaultResponseHeaders ?? [];
        var id = context.Response.Headers.ContainsKey(RequestIds.HeaderName) ? state?.RequestIdText() : null;
        List<string> parts = [];
        foreach (var operation in headers)
        {
            var value = Header(context, operation.Name);
            parts.Add(value is null ? operation.Name : $"{operation.Name}={(value == id ? Own : value)}");
        }

        // Every custom rule's header is a column too, the ones that share a built-in's name (Server) once.
        foreach (var rule in (state?.Route?.Headers ?? state?.Host?.Headers ?? CompiledHeaders.None).Response)
        {
            if (!headers.Any(operation => string.Equals(operation.Name, rule.Name, StringComparison.OrdinalIgnoreCase)))
            {
                var value = Header(context, rule.Name);
                parts.Add(value is null ? rule.Name : $"{rule.Name}={value}");
            }
        }

        return string.Join('|', parts);
    }

    /// <summary>The same, for a compiled list where the request ID is a placeholder.</summary>
    private static string SetOf(ResponseHeaderOperation[] headers) => string.Join(
        '|',
        headers.Select(operation => operation.Kind switch
        {
            ResponseHeaderKind.NotSent or ResponseHeaderKind.LeaveToUpstream or ResponseHeaderKind.Remove => operation.Name,
            _ => ResponseHeaders.IsRequestId(operation) ? $"{operation.Name}={Own}" : $"{operation.Name}={operation.Value}",
        }));

    /// <summary>A header's value on the response, or null when it isn't there.</summary>
    private static string? Header(HttpContext context, string name) =>
        context.Response.Headers.TryGetValue(name, out var value) && value.Count > 0 ? value.ToString() : null;

    /// <summary>What the host's compiled operation for one header does.</summary>
    private ResponseHeaderKind KindOf(string hostName, string name) =>
        Assert.Single(_store.Current.Hosts.Find(hostName)!.ResponseHeaders, h => h.Name == name).Kind;

    private ResponseTransformContext Transform(
        DefaultHttpContext context, string hostName, bool headersCopied, HttpResponseMessage? proxy = default, string? route = null)
    {
        var host = _store.Current.Hosts.Find(hostName);
        GateRequestState state = new() { Host = host, IsHttps = true, Route = route is null ? null : host!.Match(route) };
        context.Features.Set<IGateFeature>(state);
        return new ResponseTransformContext
        {
            HttpContext = context,
            ProxyResponse = proxy,
            HeadersCopied = headersCopied,
        };
    }

    /// <summary>The two settings a host's header list is compiled from.</summary>
    private sealed record HostSettingsParts
    {
        public HostHstsSpec? Hsts { get; init; }

        public HostServerHeaderSpec? Server { get; init; }
    }

    private sealed class OneCertificate(ServerCertificate certificate) : IServerCertificateResolver
    {
        public bool TryResolve(CertificateId id, [NotNullWhen(true)] out ServerCertificate? resolved)
        {
            resolved = certificate;
            return true;
        }
    }
}
