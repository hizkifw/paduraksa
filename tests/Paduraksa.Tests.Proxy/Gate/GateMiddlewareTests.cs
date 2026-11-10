using System.Net;

using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

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
                Id = TestIds.Host("app"),
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
            new HostSpec { Id = TestIds.Host("old"), Names = ["old.test"], Kind = HostKind.Redirect, Redirect = new RedirectSpec { Target = "https://new.test", StatusCode = 308 } },
            new HostSpec { Id = TestIds.Host("soon"), Names = ["soon.test"], Kind = HostKind.Respond, Respond = new RespondSpec { StatusCode = 503, Body = "soon" } },
            ProxyHost("locked", "locked.test", Route("locked-api", "api", "/api")) with { ForceHttps = true, Policies = [LanOnly] },
            new HostSpec { Id = TestIds.Host("locked-old"), Names = ["locked-old.test"], Kind = HostKind.Redirect, Policies = [LanOnly], Redirect = new RedirectSpec { Target = "https://new.test" } },
            new HostSpec { Id = TestIds.Host("locked-soon"), Names = ["locked-soon.test"], Kind = HostKind.Respond, Policies = [LanOnly], Respond = new RespondSpec { StatusCode = 503, Body = "soon" } },
        ],
    };

    private static IpAllowListPolicySpec LanOnly => new(["10.0.0.5"]);

    private readonly SnapshotStore _store = new();
    private readonly GateMiddleware _gate;

    public GateMiddlewareTests()
    {
        _store.Publish(Bound(Config, options: Port8443));
        _gate = new GateMiddleware(_store, new TrustedProxyClientAddressSource());
    }

    public void Dispose() => _store.Dispose();

    [Theory]
    [InlineData("app.test", "/api/items", "app-api")]
    [InlineData("APP.test:8080", "/API", "app-api")]
    [InlineData("x.app.test", "/", "app-root")]
    [InlineData("app.test", "/health", "app-health")]
    [InlineData("app.test", "/health/deep", "app-root")]
    [InlineData("secure.test", "/x", "secure-root")]
    public void ForwardsWithTheRoutesEndpointFromTheSameSnapshot(string host, string path, string route)
    {
        var context = TestContexts.Create(host, path, scheme: host.StartsWith("secure", StringComparison.Ordinal) ? "https" : "http");

        var decision = _gate.Decide(context);

        Assert.Equal(GateAction.Forward, decision.Action);
        Assert.Equal(TestIds.Route(route).ToString(), context.GetEndpoint()!.DisplayName);
        var state = context.Features.Get<IGateFeature>()!.State;
        Assert.Same(_store.Current, state.Snapshot);
        Assert.Equal(TestIds.Route(route), state.Route!.Id);
        Assert.Same(state.Snapshot!.YarpEndpoints[state.Route.Slot], context.GetEndpoint());
        Assert.Equal(IpKey.FromIPAddress(IPAddress.Loopback), state.ClientAddress);
    }

    [Theory]
    [InlineData("unknown.test")]
    [InlineData("a.b.app.test")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownHostsGetTheDefaultBehaviour(string? host)
    {
        var context = TestContexts.Create(host);

        var decision = _gate.Decide(context);

        Assert.Equal(GateAction.Respond, decision.Action);
        Assert.Same(_store.Current.Default.Response, decision.Response);
        Assert.Null(context.GetEndpoint());
        Assert.Null(context.Features.Get<IGateFeature>()!.State.Host);
    }

    [Fact]
    public void UnknownHostsCanBeClosed()
    {
        _store.Publish(Bound(Config with { Defaults = new DefaultsSpec { UnknownHost = UnknownHostBehaviour.Close } }, generation: 2));

        Assert.Equal(GateAction.Abort, _gate.Decide(TestContexts.Create("unknown.test")).Action);
    }

    [Fact]
    public void AProxyHostWithoutAMatchingRouteAnswers404()
    {
        var decision = _gate.Decide(TestContexts.Create("narrow.test", "/other"));

        Assert.Equal(GateAction.Respond, decision.Action);
        Assert.Same(_store.Current.NoRoute, decision.Response);
    }

    [Theory]
    [InlineData("GET", 301)]
    [InlineData("HEAD", 301)]
    [InlineData("POST", 308)]
    public void PlainHttpToAForcedHttpsHostIsRedirected(string method, int status)
    {
        var context = TestContexts.Create("secure.test", "/x", method);

        var decision = _gate.Decide(context);

        Assert.Equal(GateAction.Respond, decision.Action);
        Assert.Equal(status, decision.Response!.StatusCode);
        ResponseWriter.ApplyHeaders(context, decision.Response);
        Assert.Equal("https://secure.test:8443/x", context.Response.Headers.Location);
        Assert.Null(context.GetEndpoint());
    }

    [Fact]
    public void RedirectAndRespondHostsAnswerFromPrecomputedResponses()
    {
        var redirect = _gate.Decide(TestContexts.Create("old.test", "/a"));
        var respond = _gate.Decide(TestContexts.Create("soon.test", "/a"));

        Assert.Equal(308, redirect.Response!.StatusCode);
        Assert.Same(_store.Current.Hosts.Find("old.test")!.FixedResponse, redirect.Response);
        Assert.Equal(503, respond.Response!.StatusCode);
    }

    [Fact]
    public void PolicyRespondAndDropStopTheChain()
    {
        var denied = _gate.Decide(TestContexts.Create("app.test", "/blocked/x"));
        var dropped = _gate.Decide(TestContexts.Create("app.test", "/drop"));

        Assert.Equal(GateAction.Respond, denied.Action);
        Assert.Equal(451, denied.Response!.StatusCode);
        // A custom deny body is the operator's text, not the built-in page.
        Assert.Null(denied.Response.HtmlPage);
        Assert.Equal(GateAction.Abort, dropped.Action);
    }

    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("::ffff:10.0.0.5", true)]
    [InlineData("10.0.0.6", false)]
    [InlineData("2001:db8::5", false)]
    public void HostLevelAllowListAppliesToEveryRoute(string remote, bool allowed)
    {
        var context = TestContexts.Create("app.test", "/api", remote: IPAddress.Parse(remote));

        var decision = _gate.Decide(context);

        Assert.Equal(allowed ? GateAction.Forward : GateAction.Respond, decision.Action);
        if (!allowed)
        {
            Assert.Equal(403, decision.Response!.StatusCode);
        }
    }

    [Theory]
    [InlineData("locked.test", "http", "/api")]
    [InlineData("locked.test", "http", "/no-such-route")]
    [InlineData("locked.test", "https", "/no-such-route")]
    [InlineData("locked-old.test", "http", "/")]
    [InlineData("locked-soon.test", "http", "/")]
    public void HostPoliciesRunBeforeRedirectsFixedAnswersAndRouting(string host, string scheme, string path)
    {
        var blocked = _gate.Decide(TestContexts.Create(host, path, scheme: scheme, remote: IPAddress.Parse("203.0.113.9")));

        Assert.Equal(GateAction.Respond, blocked.Action);
        Assert.Equal(403, blocked.Response!.StatusCode);
        Assert.NotNull(blocked.Response.HtmlPage);
    }

    [Theory]
    [InlineData("locked.test", "http", "/api", 301)]
    [InlineData("locked.test", "https", "/no-such-route", 404)]
    [InlineData("locked-old.test", "http", "/", 301)]
    [InlineData("locked-soon.test", "http", "/", 503)]
    public void AllowedClientsGetTheHostsUsualAnswer(string host, string scheme, string path, int status)
    {
        var allowed = _gate.Decide(TestContexts.Create(host, path, scheme: scheme, remote: IPAddress.Parse("10.0.0.5")));

        Assert.Equal(status, allowed.Response!.StatusCode);
    }

    [Fact]
    public void HostPoliciesSeeNoRouteAndRoutePoliciesSeeTheirRoute()
    {
        RouteProbe hostProbe = new();
        RouteProbe routeProbe = new();
        CompiledRoute route = new() { Id = TestIds.Route("r"), MatchKind = RouteMatchKind.Prefix, Path = "/", Slot = 0, Policies = [routeProbe] };
        CompiledHost host = new() { Index = 0, Id = TestIds.Host("h"), Kind = HostKind.Proxy, Routes = [route], Policies = [hostProbe] };
        HostTable.Builder table = new();
        Assert.True(table.TryAdd("probe.test", host, out _));
        using SnapshotStore store = new();
        store.Publish(new RuntimeSnapshot
        {
            Generation = 0,
            Revision = TestIds.Revision("probe"),
            Hosts = table.Build(),
            HostsByIndex = [host],
            Yarp = Paduraksa.Proxy.Yarp.YarpConfig.Empty,
            RouteIdBySlot = [route.Id],
            YarpEndpoints = [],
            Default = DefaultHostBehaviour.NotFound(),
        }.Bind(1, [new Endpoint(static _ => Task.CompletedTask, null, route.Id.ToString())]));

        Assert.Equal(GateAction.Forward, new GateMiddleware(store, new TrustedProxyClientAddressSource()).Decide(TestContexts.Create("probe.test", "/x")).Action);
        Assert.Equal([null], hostProbe.SeenRoutes);
        Assert.Equal([route.Id], routeProbe.SeenRoutes);
    }

    [Fact]
    public void TheClientAddressComesFromTheSeam()
    {
        ClientIpResolution resolved = new(IpKey.Parse("10.0.0.5"), ClientIpSource.CfConnectingIp, ClientIpOutcome.FromHeader, CloudflareHeadersTrusted: true);
        GateMiddleware gate = new(_store, new FixedAddress(resolved));
        var context = TestContexts.Create("app.test", "/api", remote: IPAddress.Parse("203.0.113.9"));

        Assert.Equal(GateAction.Forward, gate.Decide(context).Action);
        Assert.Equal(resolved, context.Features.Get<IGateFeature>()!.State.ClientIp);
        Assert.Equal(resolved.Address, context.Features.Get<IGateFeature>()!.State.ClientAddress);
    }

    [Fact]
    public void PerRequestStateIsPooledAndReset()
    {
        var context = TestContexts.Create("app.test", "/api");
        _gate.Decide(context);
        var first = context.Features.Get<IGateFeature>()!.State;
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
        var context = TestContexts.Create("app.test", "/api");
        context.Features.Set<IPersistentStateFeature>(null);

        Assert.Equal(GateAction.Forward, _gate.Decide(context).Action);
        Assert.NotNull(context.Features.Get<IGateFeature>());
    }

    [Fact]
    public void ARequestKeepsTheSnapshotItStartedWith()
    {
        var context = TestContexts.Create("app.test", "/api");
        _gate.Decide(context);
        var seen = context.Features.Get<IGateFeature>()!.State.Snapshot!;
        var endpoint = context.GetEndpoint()!;

        _store.Publish(Bound(Config, generation: 2));

        Assert.NotSame(seen, _store.Current);
        Assert.Same(seen, context.Features.Get<IGateFeature>()!.State.Snapshot);
        Assert.Contains(endpoint, seen.YarpEndpoints);
        Assert.DoesNotContain(endpoint, _store.Current.YarpEndpoints);
    }

    [Fact]
    public async Task InvokeForwardsRespondsOrAborts()
    {
        var nextCalls = 0;
        RequestDelegate next = _ =>
        {
            nextCalls++;
            return Task.CompletedTask;
        };

        await _gate.InvokeAsync(TestContexts.Create("app.test", "/api"), next);
        Assert.Equal(1, nextCalls);

        var denied = TestContexts.Create("app.test", "/blocked");
        await _gate.InvokeAsync(denied, next);
        Assert.Equal(451, denied.Response.StatusCode);
        Assert.Equal(1, nextCalls);

        var dropped = TestContexts.Create("app.test", "/drop");
        AbortProbe probe = new();
        dropped.Features.Set<IHttpRequestLifetimeFeature>(probe);
        await _gate.InvokeAsync(dropped, next);
        Assert.True(probe.Aborted);
        Assert.Equal(1, nextCalls);
    }

    [Fact]
    public void ForwardDecisionDoesNotAllocate()
    {
        var context = TestContexts.Create("x.app.test:8443", "/api/items/42");
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
                foreach (var context in s.contexts)
                {
                    _ = s._gate.Decide(context);
                }
            },
            "gate with non-canonical paths (stack and pooled buffers)");
        Assert.Equal(451, _gate.Decide(TestContexts.Create("app.test", "//blocked/x")).Response!.StatusCode);
        Assert.Equal(TestIds.Route("app-api").ToString(), Decided(_gate, TestContexts.Create("app.test", "/api;v=1/items")));
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
                foreach (var context in s.contexts)
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

    private sealed class RouteProbe : IRequestPolicy
    {
        public List<RouteId?> SeenRoutes { get; } = [];

        public PolicyDecision Evaluate(in RequestView request)
        {
            SeenRoutes.Add(request.State.Route?.Id);
            return PolicyDecision.Continue;
        }
    }

    private sealed class FixedAddress(ClientIpResolution resolution) : IClientAddressSource
    {
        public ClientIpResolution Resolve(HttpContext context, RuntimeSnapshot snapshot) => resolution;
    }

    private sealed class AbortProbe : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }

        public CancellationToken RequestAborted { get; set; }

        public void Abort() => Aborted = true;
    }
}
