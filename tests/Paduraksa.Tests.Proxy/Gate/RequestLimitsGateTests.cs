using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Gate;

/// <summary>
/// The request limits through the real gate: what each limit refuses and with which status, that the host's own limits apply
/// after its policy chain and before its redirect, that a route's body limit replaces the host's, that every answer carries
/// the host's response headers, and that none of it allocates.
/// </summary>
public sealed class RequestLimitsGateTests : IDisposable
{
    private const long BodyLimit = 10 * 1024 * 1024;

    private static readonly AccessListSpec Locked = TestAccessLists.Allow("locked", "10.9.9.9");

    private static readonly HostRequestLimitsSpec Limits = new()
    {
        MaxBodySize = BodyLimit,
        MaxHeaderCount = 12,
        MaxHeaderBytes = 2048,
        MaxUrlLength = 512,
    };

    private static readonly ProxyConfigModel Config = new()
    {
        Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
        AccessLists = [Locked],
        Hosts =
        [
            ProxyHost(
                "limits", "limits.test",
                Route("limits-upload", "web", "/upload") with { MaxBodySize = 100L * 1024 * 1024, MaxBodySizeRouteOverride = true },
                Route("limits-small", "web", "/small") with { MaxBodySize = 1024, MaxBodySizeRouteOverride = true },
                Route("limits-open", "web", "/open") with { MaxBodySize = RequestLimitBounds.UnlimitedBodySize, MaxBodySizeRouteOverride = true },
                Route("limits-denied", "web", "/denied") with { Policies = [new DenyPolicySpec()] },
                Route("limits-root", "web")) with
            {
                RequestLimits = Limits,
            },
            ProxyHost("secure", "secure.test", Route("secure-root", "web")) with { ForceHttps = true, RequestLimits = Limits },
            ProxyHost("locked", "locked.test", Route("locked-root", "web")) with { Policies = [Locked.Policy()], RequestLimits = Limits },
            new HostSpec
            {
                Id = TestIds.Host("soon"),
                Names = ["soon.test"],
                Kind = HostKind.Respond,
                Respond = new RespondSpec { StatusCode = 503, Body = "soon" },
                RequestLimits = Limits,
            },
            ProxyHost("plain", "plain.test", Route("plain-root", "web")),
        ],
    };

    private readonly SnapshotStore _store = new();
    private readonly GateMiddleware _gate;

    public RequestLimitsGateTests()
    {
        _store.Publish(Bound(Config, options: Port8443));
        _gate = new GateMiddleware(_store, new TrustedProxyClientAddressSource());
    }

    public void Dispose() => _store.Dispose();

    [Theory]
    [InlineData("/", BodyLimit, true)]
    [InlineData("/", BodyLimit + 1, false)]
    [InlineData("/upload", BodyLimit + 1, true)]
    [InlineData("/upload", 100L * 1024 * 1024 + 1, false)]
    [InlineData("/small", 1024, true)]
    [InlineData("/small", 1025, false)]
    [InlineData("/open", long.MaxValue, true)]
    public void ADeclaredBodyIsJudgedByTheRoutesLimitOrTheHosts(string path, long declared, bool forwarded)
    {
        var context = TestContexts.Create("limits.test", path, method: "POST");
        context.Request.ContentLength = declared;

        var decision = _gate.Decide(context);

        Assert.Equal(forwarded ? GateAction.Forward : GateAction.Respond, decision.Action);
        if (!forwarded)
        {
            Assert.Equal(413, decision.Response!.StatusCode);
            Assert.Same(RequestLimitResponses.ContentTooLarge, decision.Response);
        }
    }

    [Fact]
    public void ABodyWithNoDeclaredLengthIsLeftToTheServer()
    {
        // Chunked, or HTTP/2 without a length: nothing to judge up front, and Kestrel cuts it off at the limit the gate set.
        var context = TestContexts.Create("limits.test", "/", method: "POST");
        context.Request.Headers.TransferEncoding = "chunked";
        var feature = new BodyLimitFeature();
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);

        Assert.Equal(GateAction.Forward, _gate.Decide(context).Action);
        Assert.Equal(BodyLimit, feature.MaxRequestBodySize);
    }

    [Fact]
    public void TheGatePutsTheHostsLimitOnTheRequestAndARoutesOverItBeforeTheBodyIsRead()
    {
        // Set when the host is found, so even an answer the gate gives without a route (a redirect, a fixed answer, a 404)
        // can't make the server drain more than the host allows.
        var fixedAnswer = TestContexts.Create("soon.test", "/", method: "POST");
        var soon = new BodyLimitFeature();
        fixedAnswer.Features.Set<IHttpMaxRequestBodySizeFeature>(soon);
        Assert.Equal(503, _gate.Decide(fixedAnswer).Response!.StatusCode);
        Assert.Equal(BodyLimit, soon.MaxRequestBodySize);

        // A route's own limit replaces it; none at all is no limit for the server (null), not zero.
        var small = new BodyLimitFeature();
        var smallContext = TestContexts.Create("limits.test", "/small", method: "POST");
        smallContext.Features.Set<IHttpMaxRequestBodySizeFeature>(small);
        _gate.Decide(smallContext);
        Assert.Equal(1024, small.MaxRequestBodySize);

        var open = new BodyLimitFeature();
        var openContext = TestContexts.Create("limits.test", "/open", method: "POST");
        openContext.Features.Set<IHttpMaxRequestBodySizeFeature>(open);
        _gate.Decide(openContext);
        Assert.Null(open.MaxRequestBodySize);

        // A request whose body is already being read (or an upgrade) keeps what it has.
        var started = new BodyLimitFeature { IsReadOnly = true, MaxRequestBodySize = 7 };
        var startedContext = TestContexts.Create("limits.test", "/", method: "POST");
        startedContext.Features.Set<IHttpMaxRequestBodySizeFeature>(started);
        _gate.Decide(startedContext);
        Assert.Equal(7, started.MaxRequestBodySize);
    }

    [Fact]
    public void ARefusedDeclaredBodyCanStillBeDrainedOnlyUpToTheLimit()
    {
        var context = TestContexts.Create("limits.test", "/", method: "POST");
        context.Request.ContentLength = 5 * BodyLimit;
        var feature = new BodyLimitFeature();
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);

        Assert.Equal(413, _gate.Decide(context).Response!.StatusCode);
        Assert.Equal(BodyLimit, feature.MaxRequestBodySize);
    }

    [Fact]
    public void ATargetOverTheLimitGets414AndCountsBytesNotCharacters()
    {
        Assert.Equal(GateAction.Forward, Decide("limits.test", "/" + new string('a', 511)).Action);

        var over = Decide("limits.test", "/" + new string('a', 512));
        Assert.Equal(414, over.Response!.StatusCode);
        Assert.Same(RequestLimitResponses.UriTooLong, over.Response);

        // The raw target as sent: its query counts, and a character above ASCII is more than one byte on the wire.
        var withQuery = TestContexts.Create("limits.test", "/", rawTarget: "/?" + new string('q', 600));
        Assert.Equal(414, _gate.Decide(withQuery).Response!.StatusCode);
        var wide = TestContexts.Create("limits.test", "/", rawTarget: "/" + new string('é', 256));
        Assert.Equal(414, _gate.Decide(wide).Response!.StatusCode);
        var narrow = TestContexts.Create("limits.test", "/", rawTarget: "/" + new string('é', 255));
        Assert.Equal(GateAction.Forward, _gate.Decide(narrow).Action);
    }

    [Fact]
    public void MoreHeaderLinesThanTheHostTakesGet431()
    {
        // The Host header is a line too: 11 more make 12.
        Assert.Equal(GateAction.Forward, _gate.Decide(WithHeaders(11, "v")).Action);

        var over = _gate.Decide(WithHeaders(12, "v"));
        Assert.Equal(431, over.Response!.StatusCode);
        Assert.Same(RequestLimitResponses.HeadersTooLarge, over.Response);

        // A header sent twice is two lines: the limit counts what is on the wire, not the names.
        var twice = WithHeaders(10, "v");
        twice.Request.Headers.Append("X-Twice", new Microsoft.Extensions.Primitives.StringValues(["a", "b"]));
        Assert.Equal(431, _gate.Decide(twice).Response!.StatusCode);
    }

    [Fact]
    public void MoreHeaderBytesThanTheHostTakesGet431()
    {
        // Host (4) + limits.test (11) + X-Pad (5) + value: 2048 bytes in all is taken, one more is not.
        var value = 2048 - (4 + 11 + 5);
        Assert.Equal(GateAction.Forward, _gate.Decide(WithHeaders(0, "x", ("X-Pad", new string('p', value)))).Action);
        Assert.Equal(431, _gate.Decide(WithHeaders(0, "x", ("X-Pad", new string('p', value + 1)))).Response!.StatusCode);

        // Characters above ASCII are counted as the bytes they take on the wire.
        Assert.Equal(GateAction.Forward, _gate.Decide(WithHeaders(0, "x", ("X-Pad", new string('é', value / 2)))).Action);
        Assert.Equal(431, _gate.Decide(WithHeaders(0, "x", ("X-Pad", new string('é', (value / 2) + 1)))).Response!.StatusCode);
    }

    [Fact]
    public void TheBuiltInLimitsApplyToAHostThatSetsNone()
    {
        Assert.Equal(GateAction.Forward, Decide("plain.test", "/" + new string('a', RequestLimitBounds.DefaultMaxUrlLength - 1)).Action);
        Assert.Equal(414, Decide("plain.test", "/" + new string('a', RequestLimitBounds.DefaultMaxUrlLength)).Response!.StatusCode);
    }

    [Fact]
    public void TheHostChainRefusesFirstAndTheLimitsComeBeforeTheRedirectAndTheRoute()
    {
        // A client the host's access list refuses is told 403, whatever it sent.
        var blocked = TestContexts.Create("locked.test", "/" + new string('a', 600));
        Assert.Equal(403, _gate.Decide(blocked).Response!.StatusCode);

        // A forced redirect would echo the target; the limit answers instead.
        var redirect = TestContexts.Create("secure.test", "/" + new string('a', 600));
        Assert.Equal(414, _gate.Decide(redirect).Response!.StatusCode);
        Assert.Equal(301, _gate.Decide(TestContexts.Create("secure.test", "/")).Response!.StatusCode);

        // A fixed answer is behind the limits too: they judge every request of the host.
        Assert.Equal(414, Decide("soon.test", "/" + new string('a', 600)).Response!.StatusCode);

        // The route's own policies decide before the declared body does: a denied route answers its own refusal.
        var denied = TestContexts.Create("limits.test", "/denied", method: "POST");
        denied.Request.ContentLength = BodyLimit + 1;
        Assert.Equal(403, _gate.Decide(denied).Response!.StatusCode);
    }

    [Theory]
    [InlineData("/", 0L, 0, "ok")]
    [InlineData("/", 0L, 600, "url")]
    [InlineData("/", 0L, 20, "headers")]
    [InlineData("/", BodyLimit + 1, 0, "body")]
    public void EveryAnswerCarriesTheHostsResponseHeaders(string path, long declared, int url, string kind)
    {
        var context = TestContexts.Create("limits.test", path + (url > 0 ? new string('a', url) : string.Empty), method: "POST");
        if (declared > 0)
        {
            context.Request.ContentLength = declared;
        }

        if (kind == "headers")
        {
            for (var i = 0; i < 20; i++)
            {
                context.Request.Headers[$"X-{i}"] = "v";
            }
        }

        var decision = _gate.Decide(context);

        Assert.Equal(kind == "ok" ? GateAction.Forward : GateAction.Respond, decision.Action);
        Assert.Equal("Paduraksa", context.Response.Headers.Server.ToString());
    }

    [Fact]
    public void TheCheckPutsTheContentLengthBackAndLeavesTheHeadersAsItFoundThem()
    {
        var context = WithHeaders(3, "v");
        context.Request.ContentLength = 1234;

        _gate.Decide(context);

        Assert.Equal(1234, context.Request.ContentLength);
        Assert.Equal("1234", context.Request.Headers.ContentLength.ToString());
        Assert.Equal(["Content-Length", "Host", "X-0", "X-1", "X-2"], context.Request.Headers.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ARequestWithoutAKestrelFeatureIsJudgedAllTheSame()
    {
        // The simulator's and these tests' contexts have no body-limit feature: nothing to set, nothing thrown.
        var context = TestContexts.Create("limits.test", "/", method: "POST");
        context.Request.ContentLength = BodyLimit + 1;
        Assert.Null(context.Features.Get<IHttpMaxRequestBodySizeFeature>());
        Assert.Equal(413, _gate.Decide(context).Response!.StatusCode);
    }

    [Fact]
    public void TheChecksAllocateNothing()
    {
        // Content-Length is left out: this context keeps headers as strings, so putting one back costs here what it doesn't on
        // Kestrel (the end-to-end allocation tests cover a request with a body).
        foreach (var (name, context) in new (string, DefaultHttpContext)[]
        {
            ("within the limits", WithHeaders(5, "v")),
            ("a long target", TestContexts.Create("limits.test", "/" + new string('a', 600))),
            ("too many headers", WithHeaders(20, "v")),
            ("too many header bytes", WithHeaders(0, "x", ("X-Pad", new string('p', 3000)))),
        })
        {
            AllocAssert.Zero((_gate, context), static s => _ = s._gate.Decide(s.context), name);
        }
    }

    private GateDecision Decide(string host, string path) => _gate.Decide(TestContexts.Create(host, path));

    /// <summary>A request with <paramref name="extra"/> headers of the given value (and the pairs), besides Host.</summary>
    private static DefaultHttpContext WithHeaders(int extra, string value, params (string Name, string Value)[] more)
    {
        var context = TestContexts.Create("limits.test", "/");
        for (var i = 0; i < extra; i++)
        {
            context.Request.Headers[$"X-{i}"] = value;
        }

        foreach (var (name, headerValue) in more)
        {
            context.Request.Headers[name] = headerValue;
        }

        return context;
    }

    private sealed class BodyLimitFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; init; }

        public long? MaxRequestBodySize { get; set; } = 30_000_000;
    }
}
