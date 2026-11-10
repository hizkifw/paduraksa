using System.Net;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Config;

/// <summary>The order a host tries its routes in: the most specific path wins, whatever the list order.</summary>
public sealed class RouteOrderTests
{
    private static readonly IPAddress Lan = IPAddress.Parse("10.0.0.5");

    private static readonly ProxyConfigModel Shadowed = new()
    {
        Upstreams = [Upstream("web", "http://127.0.0.1:1/"), Upstream("api", "http://127.0.0.1:2/")],
        Hosts =
        [
            ProxyHost(
                "site",
                "site.test",
                Route("site-root", "web"),
                Route("site-api", "api", "/api") with { Policies = [new DenyPolicySpec(451, "legal\n")] }),
        ],
    };

    [Fact]
    public void ACatchAllListedFirstDoesNotShadowAMoreSpecificRoute()
    {
        var snapshot = Compile(Shadowed);
        var host = snapshot.Hosts.Find("site.test")!;

        Assert.Equal([TestIds.Route("site-api"), TestIds.Route("site-root")], host.Routes.Select(r => r.Id));
        Assert.Same(host.Routes[0], host.Match("/api/x"));

        var run = GateSimulator.Run(snapshot, new GateProbe { Host = "site.test", Path = "/api/x", ClientAddress = Lan });
        Assert.Equal(GateReason.RoutePolicy, run.Outcome.Reason);
        Assert.Equal(451, run.Outcome.StatusCode);

        var route = Assert.Single(run.Steps, step => step.Kind == GateStepKind.RouteMatch);
        Assert.Equal(TestIds.Route("site-api"), route.Route);
        Assert.Equal("/api", Fact(route, "routePath"));
        Assert.Equal("0", Fact(route, "position"));

        // The catch-all still takes everything else.
        var root = GateSimulator.Run(snapshot, new GateProbe { Host = "site.test", Path = "/other", ClientAddress = Lan });
        Assert.Equal(GateReason.Forwarded, root.Outcome.Reason);
        Assert.Equal(TestIds.Route("site-root"), Assert.Single(root.Steps, step => step.Kind == GateStepKind.RouteMatch).Route);
    }

    [Fact]
    public void AnExactRouteIsTriedBeforeAPrefixRouteOfTheSamePath()
    {
        var snapshot = Compile(new ProxyConfigModel
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/"), Upstream("api", "http://127.0.0.1:2/")],
            Hosts =
            [
                ProxyHost(
                    "site",
                    "site.test",
                    Route("site-api", "api", "/api"),
                    Route("site-exact", "web", "/api", RouteMatchKind.Exact)),
            ],
        });

        var host = snapshot.Hosts.Find("site.test")!;
        Assert.Equal([TestIds.Route("site-exact"), TestIds.Route("site-api")], host.Routes.Select(r => r.Id));
        Assert.Equal(TestIds.Route("site-exact"), host.Match("/api")!.Id);
        Assert.Equal(TestIds.Route("site-api"), host.Match("/api/x")!.Id);
    }

    [Fact]
    public void APrefixEndsAtASegmentBoundary()
    {
        var host = Compile(new ProxyConfigModel
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/"), Upstream("api", "http://127.0.0.1:2/")],
            Hosts =
            [
                ProxyHost(
                    "site",
                    "site.test",
                    Route("site-api", "api", "/api"),
                    Route("site-root", "web")),
            ],
        }).Hosts.Find("site.test")!;

        Assert.Equal(TestIds.Route("site-api"), host.Match("/api/x")!.Id);
        Assert.Equal(TestIds.Route("site-root"), host.Match("/apix")!.Id);
    }

    [Fact]
    public void AnExactRouteBeatsOnlyTheLongerPrefixesThatCannotTakeItsPath()
    {
        var host = Compile(new ProxyConfigModel
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/"), Upstream("api", "http://127.0.0.1:2/")],
            Hosts =
            [
                ProxyHost(
                    "site",
                    "site.test",
                    Route("site-deep", "api", "/a/b"),
                    Route("site-exact", "web", "/a", RouteMatchKind.Exact)),
            ],
        }).Hosts.Find("site.test")!;

        Assert.Equal(TestIds.Route("site-exact"), host.Match("/a")!.Id);
        Assert.Equal(TestIds.Route("site-deep"), host.Match("/a/b/c")!.Id);
        Assert.Null(host.Match("/a/x"));
    }

    [Fact]
    public void ATrailingSlashPrefixIsTheMoreSpecificOne()
    {
        var host = Compile(new ProxyConfigModel
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/"), Upstream("api", "http://127.0.0.1:2/")],
            Hosts =
            [
                ProxyHost(
                    "site",
                    "site.test",
                    Route("site-bare", "web", "/a"),
                    Route("site-slash", "api", "/a/")),
            ],
        }).Hosts.Find("site.test")!;

        Assert.Equal(TestIds.Route("site-slash"), host.Match("/a/x")!.Id);
        Assert.Equal(TestIds.Route("site-bare"), host.Match("/a")!.Id);
    }

    [Fact]
    public void EqualSpecificityKeepsOrderThenTheListedOrder()
    {
        // Same kind and path length: Order decides, whatever the list order.
        var ordered = Compile(new ProxyConfigModel
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
            Hosts =
            [
                ProxyHost(
                    "site",
                    "site.test",
                    Route("site-b", "web", "/a/b") with { Order = 10 },
                    Route("site-a", "web", "/a/c") with { Order = 0 }),
            ],
        }).Hosts.Find("site.test")!;
        Assert.Equal([TestIds.Route("site-a"), TestIds.Route("site-b")], ordered.Routes.Select(r => r.Id));

        // Exact routes have no length race (at most one takes a path); Order then listed decides between them.
        var exact = Compile(new ProxyConfigModel
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
            Hosts =
            [
                ProxyHost(
                    "site",
                    "site.test",
                    Route("site-long", "web", "/a/b/c", RouteMatchKind.Exact) with { Order = 10 },
                    Route("site-short", "web", "/a", RouteMatchKind.Exact) with { Order = 0 }),
            ],
        }).Hosts.Find("site.test")!;
        Assert.Equal([TestIds.Route("site-short"), TestIds.Route("site-long")], exact.Routes.Select(r => r.Id));

        // Equal orders: the listed order is the last tiebreaker.
        var listed = Compile(new ProxyConfigModel
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
            Hosts =
            [
                ProxyHost(
                    "site",
                    "site.test",
                    Route("site-first", "web", "/same"),
                    Route("site-second", "web", "/same")),
            ],
        }).Hosts.Find("site.test")!;
        Assert.Equal([TestIds.Route("site-first"), TestIds.Route("site-second")], listed.Routes.Select(r => r.Id));
        Assert.Equal(TestIds.Route("site-first"), listed.Match("/same")!.Id);
    }

    private static string Fact(GateStep step, string name) => Assert.Single(step.Facts, fact => fact.Name == name).Value;
}
