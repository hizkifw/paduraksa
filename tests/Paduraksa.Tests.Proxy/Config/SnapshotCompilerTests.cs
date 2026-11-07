using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;
using Paduraksa.Tests.Proxy.Testing;

using Yarp.ReverseProxy.Configuration;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Config;

public sealed class SnapshotCompilerTests
{
    private static readonly ProxyConfigModel Sample = new()
    {
        Revision = "rev-1",
        Upstreams =
        [
            Upstream("web", "http://10.0.0.1:8080/", "http://10.0.0.2:8080/") with { LoadBalancing = "RoundRobin", SessionAffinity = true, PassiveHealth = true },
            Upstream("api", "http://10.0.0.3:9000/") with { MaxConnectionsPerServer = 32 },
        ],
        Hosts =
        [
            new HostSpec
            {
                Key = "site",
                Names = ["example.com", "*.example.com"],
                ForceHttps = true,
                Policies = [new IpAllowListPolicySpec(["192.0.2.1", "2001:db8::1"])],
                Routes =
                [
                    Route("site-root", "web") with { Order = 10 },
                    Route("site-api", "api", "/api") with { Policies = [new DenyPolicySpec(451, "legal\n")] },
                ],
            },
            new HostSpec { Key = "old", Names = ["old.test"], Kind = HostKind.Redirect, Redirect = new RedirectSpec { Target = "https://example.com/" } },
            new HostSpec { Key = "soon", Names = ["soon.test"], Kind = HostKind.Respond, Respond = new RespondSpec { StatusCode = 503, Body = "coming soon" } },
            new HostSpec { Key = "off", Names = ["off.test"], Enabled = false, Routes = [Route("off-root", "web")] },
        ],
        Defaults = new DefaultsSpec { UnknownHost = UnknownHostMode.MisdirectedRequest },
    };

    [Fact]
    public void CompilesHostsRoutesAndYarpConfig()
    {
        RuntimeSnapshot snapshot = Compile(Sample, Port8443);

        Assert.Equal(0, snapshot.Generation);
        Assert.Equal("rev-1", snapshot.Revision);
        Assert.Empty(snapshot.YarpEndpoints);
        Assert.Equal(["site", "old", "soon"], snapshot.HostsByIndex.Select(h => h.Key));
        Assert.Equal([0, 1, 2], snapshot.HostsByIndex.Select(h => h.Index));
        Assert.Equal(421, snapshot.Default.Response!.StatusCode);

        CompiledHost site = snapshot.Hosts.Find("www.example.com")!;
        Assert.Same(site, snapshot.Hosts.Find("example.com"));
        Assert.Null(snapshot.Hosts.Find("off.test"));

        // Ordered by Order, then as listed: the /api route (Order 0) comes before the root (Order 10).
        Assert.Equal(["site-api", "site-root"], site.Routes.Select(r => r.Key));
        Assert.Equal(["site-api", "site-root"], snapshot.RouteIdBySlot.Order(StringComparer.Ordinal));
        foreach (CompiledRoute route in site.Routes)
        {
            Assert.Equal(route.Key, snapshot.RouteIdBySlot[route.Slot]);
        }

        // Host policies stay on the host (the gate runs them first); routes carry only their own.
        Assert.IsType<IpAllowListPolicy>(Assert.Single(site.Policies));
        Assert.IsType<StaticPolicy>(Assert.Single(site.Routes[0].Policies));
        Assert.Empty(site.Routes[1].Policies);
        Assert.NotNull(site.HttpsRedirect);
        Assert.Equal(":8443", site.HttpsRedirect!.Safe.Redirect!.PortSuffix);

        RouteConfig api = Assert.Single(snapshot.Yarp.Routes, r => r.RouteId == "site-api");
        Assert.Equal("api", api.ClusterId);
        Assert.Equal("/.paduraksa/r/site-api/{**rest}", api.Match.Path);
        Assert.Null(api.Match.Hosts);

        ClusterConfig web = Assert.Single(snapshot.Yarp.Clusters, c => c.ClusterId == "web");
        Assert.Equal(["web-d0", "web-d1"], web.Destinations!.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("RoundRobin", web.LoadBalancingPolicy);
        Assert.True(web.SessionAffinity!.Enabled);
        Assert.True(web.HealthCheck!.Passive!.Enabled);
        Assert.Equal(32, Assert.Single(snapshot.Yarp.Clusters, c => c.ClusterId == "api").HttpClient!.MaxConnectionsPerServer);
    }

    [Fact]
    public void ActiveHealthChecksCompileIntoTheCluster()
    {
        RuntimeSnapshot snapshot = Compile(new ProxyConfigModel
        {
            Upstreams =
            [
                Upstream("both", "http://10.0.0.1/") with { PassiveHealth = true, ActiveHealth = new ActiveHealthSpec { Path = "/healthz", Interval = TimeSpan.FromSeconds(5), Timeout = TimeSpan.FromSeconds(2) } },
                Upstream("active", "http://10.0.0.2/") with { ActiveHealth = new ActiveHealthSpec() },
            ],
        });

        HealthCheckConfig both = Assert.Single(snapshot.Yarp.Clusters, c => c.ClusterId == "both").HealthCheck!;
        Assert.True(both.Passive!.Enabled);
        Assert.True(both.Active!.Enabled);
        Assert.Equal("/healthz", both.Active.Path);
        Assert.Equal(TimeSpan.FromSeconds(5), both.Active.Interval);
        Assert.Equal(TimeSpan.FromSeconds(2), both.Active.Timeout);
        Assert.Equal("ConsecutiveFailures", both.Active.Policy);

        HealthCheckConfig active = Assert.Single(snapshot.Yarp.Clusters, c => c.ClusterId == "active").HealthCheck!;
        Assert.Null(active.Passive);
        Assert.Equal("/", active.Active!.Path);
    }

    [Fact]
    public void RedirectAndRespondHostsGetPrecomputedAnswers()
    {
        RuntimeSnapshot snapshot = Compile(Sample);

        PrecomputedResponse redirect = snapshot.Hosts.Find("old.test")!.FixedResponse!;
        Assert.Equal(301, redirect.StatusCode);
        Assert.Equal(new RedirectTarget("https://example.com", false, string.Empty, true), redirect.Redirect);

        PrecomputedResponse respond = snapshot.Hosts.Find("soon.test")!.FixedResponse!;
        Assert.Equal(503, respond.StatusCode);
        Assert.Equal("coming soon"u8.ToArray(), respond.Body);

        HostSpec fixedRedirect = new()
        {
            Key = "r",
            Names = ["r.test"],
            Kind = HostKind.Redirect,
            Redirect = new RedirectSpec { Target = "https://x.test/landing?a=1", StatusCode = 302, PreservePath = false },
        };
        PrecomputedResponse fixedResponse = Compile(new ProxyConfigModel { Hosts = [fixedRedirect] }).HostsByIndex[0].FixedResponse!;
        Assert.Equal("https://x.test/landing?a=1", fixedResponse.Location);
    }

    [Fact]
    public void YarpConfigIsAPureFunctionOfContent()
    {
        RuntimeSnapshot first = Compile(Sample);
        RuntimeSnapshot again = Compile(Sample with { Revision = "rev-2" });
        RuntimeSnapshot policyOnly = Compile(Sample with
        {
            Hosts = [Sample.Hosts[0] with { Policies = [new DropPolicySpec()], ForceHttps = false }, .. Sample.Hosts.Skip(1)],
        });
        RuntimeSnapshot moved = Compile(Sample with
        {
            Hosts = [Sample.Hosts[0] with { Routes = [Route("site-root", "api"), Sample.Hosts[0].Routes[1]] }, .. Sample.Hosts.Skip(1)],
        });

        Assert.True(first.Yarp.ContentEquals(again.Yarp));
        Assert.True(first.Yarp.ContentEquals(policyOnly.Yarp));
        Assert.False(first.Yarp.ContentEquals(moved.Yarp));
    }

    [Fact]
    public void EmptyConfigCompilesToAnEmptySnapshot()
    {
        RuntimeSnapshot snapshot = Compile(ProxyConfigModel.Empty);

        Assert.Equal(0, snapshot.Hosts.Count);
        Assert.True(snapshot.Yarp.IsEmpty);
        Assert.Empty(snapshot.RouteIdBySlot);
        Assert.Equal(404, snapshot.Default.Response!.StatusCode);
        Assert.Equal(DefaultHostAction.Close, Compile(new ProxyConfigModel { Defaults = new DefaultsSpec { UnknownHost = UnknownHostMode.Close } }).Default.Action);
    }

    public static TheoryData<string, ProxyConfigModel, string> InvalidConfigs => new()
    {
        { "duplicate host key", Hosts(Respond("a", "a.test"), Respond("a", "b.test")), "duplicate host key" },
        { "duplicate name across hosts", Hosts(Respond("a", "x.test"), Respond("b", "X.TEST.")), "duplicate host name 'x.test'" },
        { "invalid name", Hosts(Respond("a", "a.*.test")), "wildcard" },
        { "unknown TLS setting", Hosts(Respond("a", "a.test") with { Tls = new HostTlsSpec { TcpAlpn = (TcpAlpn)7 } }), "unknown TLS setting" },
        { "single-label wildcard parent", Hosts(Respond("a", "*.localhost")), "at least two labels" },
        { "duplicate name in a disabled host", Hosts(Respond("a", "a.test") with { Names = ["x.test", "X.test."], Enabled = false }), "duplicate host name 'x.test'" },
        { "no names", Hosts(new HostSpec { Key = "a", Names = [], Kind = HostKind.Respond, Respond = new RespondSpec() }), "at least one name" },
        { "invalid key", Hosts(Respond("a b", "a.test")), "invalid key" },
        { "dangling upstream", WithWeb(ProxyHost("a", "a.test", Route("r", "missing"))), "upstream 'missing' doesn't exist" },
        { "duplicate route key", WithWeb(ProxyHost("a", "a.test", Route("r", "web")), ProxyHost("b", "b.test", Route("R", "web"))), "duplicate route key" },
        { "proxy without routes", WithWeb(ProxyHost("a", "a.test")), "at least one route" },
        { "route path", WithWeb(ProxyHost("a", "a.test", Route("r", "web", "api"))), "must start with '/'" },
        { "route query", WithWeb(ProxyHost("a", "a.test", Route("r", "web", "/a?b"))), "no query" },
        { "encoded route path", WithWeb(ProxyHost("a", "a.test", Route("r", "web", "/a%20b"))), "without percent-encoding" },
        { "double slash", WithWeb(ProxyHost("a", "a.test", Route("r", "web", "/a//b"))), "use '/a/b'" },
        { "segment parameter", WithWeb(ProxyHost("a", "a.test", Route("r", "web", "/a;v=1"))), "use '/a'" },
        { "backslash", WithWeb(ProxyHost("a", "a.test", Route("r", "web", "/a\\b"))), "use '/a/b'" },
        { "dot segment", WithWeb(ProxyHost("a", "a.test", Route("r", "web", "/a/./b"))), "use '/a/b'" },
        { "trailing dot", WithWeb(ProxyHost("a", "a.test", Route("r", "web", "/admin."))), "use '/admin'" },
        { "routes on respond host", WithWeb(Respond("a", "a.test") with { Routes = [Route("r", "web")] }), "only proxy hosts have routes" },
        { "redirect without target", Hosts(new HostSpec { Key = "a", Names = ["a.test"], Kind = HostKind.Redirect }), "needs a redirect target" },
        { "relative redirect", Hosts(new HostSpec { Key = "a", Names = ["a.test"], Kind = HostKind.Redirect, Redirect = new RedirectSpec { Target = "/x" } }), "absolute http or https URL" },
        { "redirect status", Hosts(new HostSpec { Key = "a", Names = ["a.test"], Kind = HostKind.Redirect, Redirect = new RedirectSpec { Target = "https://x.test", StatusCode = 200 } }), "redirect status 200" },
        { "respond status", Hosts(new HostSpec { Key = "a", Names = ["a.test"], Kind = HostKind.Respond, Respond = new RespondSpec { StatusCode = 99 } }), "respond status 99" },
        { "duplicate upstream", new ProxyConfigModel { Upstreams = [Upstream("u", "http://a/"), Upstream("U", "http://b/")] }, "duplicate upstream key" },
        { "upstream without destinations", new ProxyConfigModel { Upstreams = [new UpstreamSpec { Key = "u", Destinations = [] }] }, "at least one destination" },
        { "bad destination address", new ProxyConfigModel { Upstreams = [Upstream("u", "ftp://a/")] }, "absolute http or https address" },
        { "duplicate destination", new ProxyConfigModel { Upstreams = [new UpstreamSpec { Key = "u", Destinations = [new("d", "http://a/"), new("D", "http://b/")] }] }, "duplicate destination key" },
        { "bad allow-list address", Hosts(Respond("a", "a.test") with { Policies = [new IpAllowListPolicySpec(["10.0.0.0/8", "10.0.0.1/8"])] }), "'10.0.0.1/8' isn't an IP address or CIDR (host bits" },
        { "bad deny status", Hosts(Respond("a", "a.test") with { Policies = [new DenyPolicySpec(42)] }), "deny status 42" },
        { "respond 204 with body", Hosts(new HostSpec { Key = "a", Names = ["a.test"], Kind = HostKind.Respond, Respond = new RespondSpec { StatusCode = 204, Body = "x" } }), "a 204 response can't have a body" },
        { "respond 304 with body", Hosts(new HostSpec { Key = "a", Names = ["a.test"], Kind = HostKind.Respond, Respond = new RespondSpec { StatusCode = 304, Body = "x" } }), "a 304 response can't have a body" },
        { "active health path", new ProxyConfigModel { Upstreams = [Upstream("u", "http://a/") with { ActiveHealth = new ActiveHealthSpec { Path = "health" } }] }, "active health check needs a path" },
        { "active health interval", new ProxyConfigModel { Upstreams = [Upstream("u", "http://a/") with { ActiveHealth = new ActiveHealthSpec { Interval = TimeSpan.Zero } }] }, "active health check needs" },
        { "deny 205 with body", Hosts(Respond("a", "a.test") with { Policies = [new DenyPolicySpec(205)] }), "a 205 deny response can't have a body" },
    };

    [Theory]
    [MemberData(nameof(InvalidConfigs))]
    public void InvalidConfigsAreRejectedWithAReason(string because, ProxyConfigModel model, string expected)
    {
        CompileResult result = SnapshotCompiler.Compile(model);

        Assert.False(result.Succeeded, because);
        Assert.Null(result.Snapshot);
        Assert.Contains(result.Errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void BodilessStatusesCompileWithAnEmptyBody()
    {
        RuntimeSnapshot snapshot = Compile(Hosts(
            new HostSpec { Key = "a", Names = ["a.test"], Kind = HostKind.Respond, Respond = new RespondSpec { StatusCode = 204 } },
            Respond("b", "b.test") with { Policies = [new DenyPolicySpec(304, string.Empty)] }));

        Assert.Empty(snapshot.Hosts.Find("a.test")!.FixedResponse!.Body);
        Assert.IsType<StaticPolicy>(snapshot.Hosts.Find("b.test")!.Policies[0]);
    }

    [Fact]
    public void ADisabledHostMayShareANameWithAnotherHost()
    {
        RuntimeSnapshot snapshot = Compile(Hosts(Respond("staged", "a.test") with { Enabled = false }, Respond("live", "A.TEST.")));

        Assert.Equal("live", snapshot.Hosts.Find("a.test")!.Key);
    }

    [Fact]
    public void EveryErrorIsReported()
    {
        CompileResult result = SnapshotCompiler.Compile(Hosts(Respond("a", "x.test"), Respond("a", "x.test"), Respond("b c", "y.test")));

        Assert.Equal(3, result.Errors.Count);
    }

    private static ProxyConfigModel Hosts(params HostSpec[] hosts) => new() { Hosts = hosts };

    private static ProxyConfigModel WithWeb(params HostSpec[] hosts) => new() { Hosts = hosts, Upstreams = [Upstream("web", "http://127.0.0.1:1/")] };

    private static HostSpec Respond(string key, string name) =>
        new() { Key = key, Names = [name], Kind = HostKind.Respond, Respond = new RespondSpec { Body = key } };
}
