using System.Net;

using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Gate;

public sealed class GateMiddlewareTests : IDisposable
{
    private static readonly ProxyConfigModel Config = new()
    {
        Upstreams = [Upstream("web", "http://127.0.0.1:1/"), Upstream("api", "http://127.0.0.1:2/")],
        Hosts =
        [
            new HostSpec
            {
                Key = "app",
                Names = ["app.test", "*.app.test"],
                Policies = [new IpAllowListPolicySpec(["127.0.0.1", "10.0.0.5"])],
                Routes =
                [
                    Route("app-api", "api", "/api"),
                    Route("app-health", "api", "/health", RouteMatchKind.Exact),
                    Route("app-blocked", "web", "/blocked") with { Policies = [new DenyPolicySpec(451, "legal\n")] },
                    Route("app-drop", "web", "/drop") with { Policies = [new DropPolicySpec()] },
                    Route("app-root", "web"),
                ],
            },
            ProxyHost("narrow", "narrow.test", Route("narrow-api", "api", "/api")),
            ProxyHost("secure", "secure.test", Route("secure-root", "web")) with { ForceHttps = true },
            new HostSpec { Key = "old", Names = ["old.test"], Kind = HostKind.Redirect, Redirect = new RedirectSpec { Target = "https://new.test", StatusCode = 308 } },
            new HostSpec { Key = "soon", Names = ["soon.test"], Kind = HostKind.Respond, Respond = new RespondSpec { StatusCode = 503, Body = "soon" } },
        ],
        Defaults = new DefaultsSpec { PublicHttpsPort = 8443 },
    };

    private readonly SnapshotStore _store = new();
    private readonly GateMiddleware _gate;

    public GateMiddlewareTests()
    {
        _store.Publish(Bound(Config));
        _gate = new GateMiddleware(_store, RemoteIpClientAddressSource.Instance);
    }

    public void Dispose() => _store.Dispose();

    [Theory]
    [InlineData("app.test", "/api/items", "app-api")]
    [InlineData("APP.test:8080", "/API", "app-api")]
    [InlineData("x.app.test", "/", "app-root")]
    [InlineData("app.test", "/health", "app-health")]
    [InlineData("app.test", "/health/deep", "app-root")]
    [InlineData("secure.test", "/x", "secure-root")]
    public void ForwardsWithTheRoutesEndpointFromTheSameSnapshot(string host, string path, string routeId)
    {
        DefaultHttpContext context = TestContexts.Create(host, path, scheme: host.StartsWith("secure", StringComparison.Ordinal) ? "https" : "http");

        GateDecision decision = _gate.Decide(context);

        Assert.Equal(GateAction.Forward, decision.Action);
        Assert.Equal(routeId, context.GetEndpoint()!.DisplayName);
        GateRequestState state = context.Features.Get<IGateFeature>()!.State;
        Assert.Same(_store.Current, state.Snapshot);
        Assert.Equal(routeId, state.Route!.Key);
        Assert.Same(state.Snapshot!.YarpEndpoints[state.Route.Slot], context.GetEndpoint());
        Assert.Equal(ClientAddressKey.From(IPAddress.Loopback), state.ClientAddress);
    }

    [Theory]
    [InlineData("unknown.test")]
    [InlineData("a.b.app.test")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownHostsGetTheDefaultBehaviour(string? host)
    {
        DefaultHttpContext context = TestContexts.Create(host);

        GateDecision decision = _gate.Decide(context);

        Assert.Equal(GateAction.Respond, decision.Action);
        Assert.Same(_store.Current.Default.Response, decision.Response);
        Assert.Null(context.GetEndpoint());
        Assert.Null(context.Features.Get<IGateFeature>()!.State.Host);
    }

    [Fact]
    public void UnknownHostsCanBeClosed()
    {
        _store.Publish(Bound(Config with { Defaults = new DefaultsSpec { UnknownHost = UnknownHostMode.Close } }, generation: 2));

        Assert.Equal(GateAction.Abort, _gate.Decide(TestContexts.Create("unknown.test")).Action);
    }

    [Fact]
    public void AProxyHostWithoutAMatchingRouteAnswers404()
    {
        GateDecision decision = _gate.Decide(TestContexts.Create("narrow.test", "/other"));

        Assert.Equal(GateAction.Respond, decision.Action);
        Assert.Same(_store.Current.NoRoute, decision.Response);
    }

    [Theory]
    [InlineData("GET", 301)]
    [InlineData("HEAD", 301)]
    [InlineData("POST", 308)]
    public void PlainHttpToAForcedHttpsHostIsRedirected(string method, int status)
    {
        DefaultHttpContext context = TestContexts.Create("secure.test", "/x", method);

        GateDecision decision = _gate.Decide(context);

        Assert.Equal(GateAction.Respond, decision.Action);
        Assert.Equal(status, decision.Response!.StatusCode);
        ResponseWriter.ApplyHeaders(context, decision.Response);
        Assert.Equal("https://secure.test:8443/x", context.Response.Headers.Location);
        Assert.Null(context.GetEndpoint());
    }

    [Fact]
    public void RedirectAndRespondHostsAnswerFromPrecomputedResponses()
    {
        GateDecision redirect = _gate.Decide(TestContexts.Create("old.test", "/a"));
        GateDecision respond = _gate.Decide(TestContexts.Create("soon.test", "/a"));

        Assert.Equal(308, redirect.Response!.StatusCode);
        Assert.Same(_store.Current.Hosts.Find("old.test")!.FixedResponse, redirect.Response);
        Assert.Equal(503, respond.Response!.StatusCode);
    }

    [Fact]
    public void PolicyRespondAndDropStopTheChain()
    {
        GateDecision denied = _gate.Decide(TestContexts.Create("app.test", "/blocked/x"));
        GateDecision dropped = _gate.Decide(TestContexts.Create("app.test", "/drop"));

        Assert.Equal(GateAction.Respond, denied.Action);
        Assert.Equal(451, denied.Response!.StatusCode);
        Assert.Equal(GateAction.Abort, dropped.Action);
    }

    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("::ffff:10.0.0.5", true)]
    [InlineData("10.0.0.6", false)]
    [InlineData("2001:db8::5", false)]
    public void HostLevelAllowListAppliesToEveryRoute(string remote, bool allowed)
    {
        DefaultHttpContext context = TestContexts.Create("app.test", "/api", remote: IPAddress.Parse(remote));

        GateDecision decision = _gate.Decide(context);

        Assert.Equal(allowed ? GateAction.Forward : GateAction.Respond, decision.Action);
        if (!allowed)
        {
            Assert.Equal(403, decision.Response!.StatusCode);
        }
    }

    [Fact]
    public void TheClientAddressComesFromTheSeam()
    {
        UInt128 resolved = ClientAddressKey.From(IPAddress.Parse("10.0.0.5"));
        GateMiddleware gate = new(_store, new FixedAddress(resolved));
        DefaultHttpContext context = TestContexts.Create("app.test", "/api", remote: IPAddress.Parse("203.0.113.9"));

        Assert.Equal(GateAction.Forward, gate.Decide(context).Action);
        Assert.Equal(resolved, context.Features.Get<IGateFeature>()!.State.ClientAddress);
    }

    [Fact]
    public void PerRequestStateIsPooledAndReset()
    {
        DefaultHttpContext context = TestContexts.Create("app.test", "/api");
        _gate.Decide(context);
        GateRequestState first = context.Features.Get<IGateFeature>()!.State;
        Assert.NotNull(first.Route);

        context.Request.Headers.Host = "unknown.test";
        _gate.Decide(context);

        Assert.Same(first, context.Features.Get<IGateFeature>()!.State);
        Assert.Null(first.Host);
        Assert.Null(first.Route);
    }

    [Fact]
    public void WorksWithoutPersistentState()
    {
        DefaultHttpContext context = TestContexts.Create("app.test", "/api");
        context.Features.Set<IPersistentStateFeature>(null);

        Assert.Equal(GateAction.Forward, _gate.Decide(context).Action);
        Assert.NotNull(context.Features.Get<IGateFeature>());
    }

    [Fact]
    public void ARequestKeepsTheSnapshotItStartedWith()
    {
        DefaultHttpContext context = TestContexts.Create("app.test", "/api");
        _gate.Decide(context);
        RuntimeSnapshot seen = context.Features.Get<IGateFeature>()!.State.Snapshot!;
        Endpoint endpoint = context.GetEndpoint()!;

        _store.Publish(Bound(Config, generation: 2));

        Assert.NotSame(seen, _store.Current);
        Assert.Same(seen, context.Features.Get<IGateFeature>()!.State.Snapshot);
        Assert.Contains(endpoint, seen.YarpEndpoints);
        Assert.DoesNotContain(endpoint, _store.Current.YarpEndpoints);
    }

    [Fact]
    public async Task InvokeForwardsRespondsOrAborts()
    {
        int nextCalls = 0;
        RequestDelegate next = _ =>
        {
            nextCalls++;
            return Task.CompletedTask;
        };

        await _gate.InvokeAsync(TestContexts.Create("app.test", "/api"), next);
        Assert.Equal(1, nextCalls);

        DefaultHttpContext denied = TestContexts.Create("app.test", "/blocked");
        await _gate.InvokeAsync(denied, next);
        Assert.Equal(451, denied.Response.StatusCode);
        Assert.Equal(1, nextCalls);

        DefaultHttpContext dropped = TestContexts.Create("app.test", "/drop");
        AbortProbe probe = new();
        dropped.Features.Set<IHttpRequestLifetimeFeature>(probe);
        await _gate.InvokeAsync(dropped, next);
        Assert.True(probe.Aborted);
        Assert.Equal(1, nextCalls);
    }

    [Fact]
    public void ForwardDecisionDoesNotAllocate()
    {
        DefaultHttpContext context = TestContexts.Create("x.app.test:8443", "/api/items/42");
        AllocAssert.Zero((_gate, context), static s => _ = s._gate.Decide(s.context), "gate forward (host + route + policy + SetEndpoint)");
    }

    [Fact]
    public void NonCanonicalPathsDoNotAllocate()
    {
        DefaultHttpContext[] contexts =
        [
            TestContexts.Create("app.test", "//blocked/x"),
            TestContexts.Create("app.test", "/api;v=1/items"),
            TestContexts.Create("app.test", "/" + new string('a', 300) + "//x"),
        ];
        AllocAssert.Zero(
            (_gate, contexts),
            static s =>
            {
                foreach (DefaultHttpContext context in s.contexts)
                {
                    _ = s._gate.Decide(context);
                }
            },
            "gate with non-canonical paths (stack and pooled buffers)");
        Assert.Equal(451, _gate.Decide(TestContexts.Create("app.test", "//blocked/x")).Response!.StatusCode);
        Assert.Equal("app-api", Decided(_gate, TestContexts.Create("app.test", "/api;v=1/items")));
    }

    [Fact]
    public void AnsweredDecisionsDoNotAllocate()
    {
        DefaultHttpContext[] contexts =
        [
            TestContexts.Create("app.test", "/blocked"),
            TestContexts.Create("app.test", "/drop"),
            TestContexts.Create("unknown.test", "/"),
            TestContexts.Create("secure.test", "/x"),
            TestContexts.Create("old.test", "/"),
            TestContexts.Create("narrow.test", "/nope"),
            TestContexts.Create("app.test", "/api", remote: IPAddress.Parse("10.9.9.9")),
        ];
        AllocAssert.Zero(
            (_gate, contexts),
            static s =>
            {
                foreach (DefaultHttpContext context in s.contexts)
                {
                    _ = s._gate.Decide(context);
                }
            },
            "gate answered decisions");
    }

    private static string? Decided(GateMiddleware gate, DefaultHttpContext context)
    {
        _ = gate.Decide(context);
        return context.GetEndpoint()?.DisplayName;
    }

    private sealed class FixedAddress(UInt128 address) : IClientAddressSource
    {
        public UInt128 GetClientAddress(HttpContext context) => address;
    }

    private sealed class AbortProbe : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }

        public CancellationToken RequestAborted { get; set; }

        public void Abort() => Aborted = true;
    }
}
