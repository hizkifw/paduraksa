using Microsoft.AspNetCore.Http;

using Paduraksa.Application.Security;
using Paduraksa.Contracts.Security;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.RateLimiting;
using Paduraksa.Proxy.Snapshot;

using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Security;

/// <summary>
/// The overview reads the counters as they are and each host's protections from the compiled snapshot: a proof-of-work
/// switch on the host or overridden by a route, the rate-limit policy of the host and the routes that differ, the
/// robots.txt mode and the allow list's size.
/// </summary>
public sealed class SecurityOverviewServiceTests
{
    private static readonly RateLimitPolicyId Standard = Id<RateLimitPolicyId>(1);
    private static readonly RateLimitPolicyId Strict = Id<RateLimitPolicyId>(2);
    private static readonly UpstreamId App = Id<UpstreamId>(1);
    private static readonly AccessListId Office = Id<AccessListId>(1);
    private static readonly AccessListId Lan = Id<AccessListId>(2);

    [Fact]
    public void TheCountersAreReadAsTheyStand()
    {
        var (service, proofOfWork, rateLimits, _) = Start();
        proofOfWork.Stats.CountChallengeServed();
        proofOfWork.Stats.CountChallengeServed();
        proofOfWork.Stats.CountSolved();
        proofOfWork.Stats.CountVerifyRejected();
        proofOfWork.Stats.CountBlockedNonBrowser();
        proofOfWork.Stats.CountMalformed();
        proofOfWork.Stats.CountBadTag();
        proofOfWork.Stats.CountExpired();
        proofOfWork.Stats.CountTooEasy();
        rateLimits.Stats.CountAllowed();
        rateLimits.Stats.CountAllowed();
        rateLimits.Stats.CountAllowed();
        rateLimits.Stats.CountLimited();

        var overview = service.Read();

        Assert.Equal(2, overview.ProofOfWork.ChallengesServed);
        Assert.Equal(1, overview.ProofOfWork.Solved);
        Assert.Equal(1, overview.ProofOfWork.VerifyRejected);
        Assert.Equal(1, overview.ProofOfWork.BlockedNonBrowser);
        Assert.Equal(new(1, 1, 1, 1), overview.ProofOfWork.TokensRejected);
        Assert.Equal(new(3, 1), overview.RateLimit);
        Assert.Empty(overview.Hosts);
    }

    [Fact]
    public void SinceIsTheStartAndSampledAtIsTheReadTime()
    {
        FakeTime time = new(new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero));
        var (service, _, _, _) = Start(time);

        time.Advance(TimeSpan.FromSeconds(90));
        var overview = service.Read();

        Assert.Equal(new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero), overview.Since);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 8, 1, 30, TimeSpan.Zero), overview.SampledAt);
    }

    [Fact]
    public void AHostWithProofOfWorkOnCountsTheRoutesThatExemptThemselves()
    {
        var (service, _, _, store) = Start();
        Publish(store, Model(Host(1, "app.test") with
        {
            ProofOfWork = new HostProofOfWorkSpec { Enabled = true, Difficulty = 18 },
            Routes =
            [
                Route(1, "/"),
                Route(2, "/api") with { ProofOfWork = new HostProofOfWorkSpec { Enabled = false, Difficulty = 18 }, ProofOfWorkRouteOverride = true },
                Route(3, "/static") with { ProofOfWork = new HostProofOfWorkSpec { Enabled = false, Difficulty = 18 }, ProofOfWorkRouteOverride = true },
            ],
        }));

        var host = Assert.Single(service.Read().Hosts);

        Assert.Equal(new ProofOfWorkProtection(true, 18, 2), host.ProofOfWork);
    }

    [Fact]
    public void AHostWithProofOfWorkOffCountsTheRoutesThatProtectThemselves()
    {
        var (service, _, _, store) = Start();
        Publish(store, Model(Host(1, "app.test") with
        {
            Routes =
            [
                Route(1, "/"),
                Route(2, "/admin") with { ProofOfWork = new HostProofOfWorkSpec { Enabled = true }, ProofOfWorkRouteOverride = true },
            ],
        }));

        var host = Assert.Single(service.Read().Hosts);

        Assert.False(host.ProofOfWork.Enabled);
        Assert.Equal(1, host.ProofOfWork.RouteOverrides);
    }

    [Fact]
    public void TheHostsPolicyIsReportedAndTheRoutesThatDifferAreGroupedByWhatTheyApplyInstead()
    {
        var (service, _, _, store) = Start();
        Publish(store, Model(Host(1, "app.test") with
        {
            RateLimit = new HostRateLimitSpec { Enabled = true, PolicyId = Standard },
            Routes =
            [
                Route(1, "/") with { RateLimit = Standard },
                Route(2, "/login") with { RateLimit = Strict, RateLimitRouteOverride = true },
                Route(3, "/auth") with { RateLimit = Strict, RateLimitRouteOverride = true },
                Route(4, "/health") with { RateLimit = null, RateLimitRouteOverride = true },
            ],
        }));

        var host = Assert.Single(service.Read().Hosts);

        Assert.Equal(new RateLimitPolicyRef(Standard, "standard"), host.RateLimit.Policy);
        Assert.Equal(2, host.RateLimit.Overrides.Count);
        var off = Assert.Single(host.RateLimit.Overrides, use => use.Policy is null);
        Assert.Equal(1, off.Routes);
        var strict = Assert.Single(host.RateLimit.Overrides, use => use.Policy is not null);
        Assert.Equal(Strict, strict.Policy!.Id);
        Assert.Equal("strict", strict.Policy.Name);
        Assert.Equal(2, strict.Routes);
    }

    [Fact]
    public void ARouteThatInheritsTheHostsPolicyIsNotAnOverride()
    {
        var (service, _, _, store) = Start();
        Publish(store, Model(Host(1, "app.test") with
        {
            RateLimit = new HostRateLimitSpec { Enabled = true, PolicyId = Standard },
            Routes = [Route(1, "/") with { RateLimit = Standard }],
        }));

        var host = Assert.Single(service.Read().Hosts);

        Assert.Empty(host.RateLimit.Overrides);
    }

    [Fact]
    public void AHostWithNoPolicyHasNoneAndShowsTheRoutesThatDo()
    {
        var (service, _, _, store) = Start();
        Publish(store, Model(Host(1, "app.test") with
        {
            Routes = [Route(1, "/"), Route(2, "/login") with { RateLimit = Strict, RateLimitRouteOverride = true }],
        }));

        var host = Assert.Single(service.Read().Hosts);

        Assert.Null(host.RateLimit.Policy);
        var use = Assert.Single(host.RateLimit.Overrides);
        Assert.Equal(Strict, use.Policy!.Id);
        Assert.Equal(1, use.Routes);
    }

    [Fact]
    public void RobotsModeIsReported()
    {
        var (service, _, _, store) = Start();
        Publish(store, Model(
            Host(1, "a.test") with { Robots = new HostRobotsSpec { Mode = RobotsMode.BlockAiCrawlers } },
            Host(2, "b.test")));

        var overview = service.Read();

        Assert.Equal(RobotsMode.BlockAiCrawlers, Assert.Single(overview.Hosts, host => host.Name == "a.test").Robots);
        Assert.Equal(RobotsMode.Passthrough, Assert.Single(overview.Hosts, host => host.Name == "b.test").Robots);
    }

    [Fact]
    public void TheHostsAccessListIsNamedWithItsUsersAndItsRangesMerged()
    {
        var (service, _, _, store) = Start();
        Publish(store, Model(
            Host(1, "a.test"),
            Host(2, "b.test") with { AccessList = new HostAccessListSpec { Enabled = true, AccessListId = Office } },
            Host(3, "c.test") with { AccessList = new HostAccessListSpec { Enabled = true, AccessListId = Lan } },
            Host(4, "d.test") with { AccessList = new HostAccessListSpec { Enabled = false, AccessListId = Lan } }));

        var overview = service.Read();

        var none = Assert.Single(overview.Hosts, host => host.Name == "a.test").AccessList;
        Assert.Null(none.List);
        Assert.Empty(none.Overrides);
        var office = Assert.Single(overview.Hosts, host => host.Name == "b.test").AccessList;
        Assert.Equal(new AccessListRef(Office, "office", 2, 2), office.List);
        var lan = Assert.Single(overview.Hosts, host => host.Name == "c.test").AccessList;
        Assert.Equal(new AccessListRef(Lan, "lan", 0, 1), lan.List);
        Assert.Null(Assert.Single(overview.Hosts, host => host.Name == "d.test").AccessList.List);
    }

    [Fact]
    public void RoutesThatUseAnotherListOrNoneAreGroupedByWhatGuardsThemInstead()
    {
        var (service, _, _, store) = Start();
        var host = Host(1, "a.test") with
        {
            AccessList = new HostAccessListSpec { Enabled = true, AccessListId = Office },
            Routes =
            [
                Route(1, "/") with { AccessList = Office },
                Route(2, "/admin") with { AccessList = Lan },
                Route(3, "/reports") with { AccessList = Lan },
                Route(4, "/public") with { AccessList = null },
            ],
        };
        Publish(store, Model(host));

        var protection = Assert.Single(service.Read().Hosts).AccessList;

        Assert.Equal("office", protection.List?.Name);
        Assert.Equal(2, protection.Overrides.Count);
        Assert.Equal(new AccessListOverride(1, null), protection.Overrides[0]);
        Assert.Equal(new AccessListOverride(2, new AccessListRef(Lan, "lan", 0, 1)), protection.Overrides[1]);
    }

    [Fact]
    public void CloudflareLockdownAndOriginPullsAreReportedOnlyWhereTheModeMakesThemReal()
    {
        var (service, _, _, store) = Start();
        var locked = new HostCloudflareSpec { OriginLockdown = OriginLockdownMode.Drop, AuthenticatedOriginPulls = true };
        ProxyConfigModel Cloudflare(TrustedProxyMode mode) => Model(Host(1, "a.test") with { Cloudflare = locked }, Host(2, "b.test")) with
        {
            Defaults = new DefaultsSpec { TrustedProxies = new TrustedProxySpec { Mode = mode, Proxies = mode == TrustedProxyMode.Generic ? ["10.0.0.0/8"] : [] } },
        };

        Publish(store, Cloudflare(TrustedProxyMode.CloudflareProxy));
        var overview = service.Read();
        var a = Assert.Single(overview.Hosts, host => host.Name == "a.test");
        Assert.Equal((OriginLockdownMode.Drop, true), (a.OriginLockdown, a.AuthenticatedOriginPulls));
        var b = Assert.Single(overview.Hosts, host => host.Name == "b.test");
        Assert.Equal((OriginLockdownMode.Off, false), (b.OriginLockdown, b.AuthenticatedOriginPulls));

        // The compiler resolves both to nothing under a mode without Cloudflare, and the page reports what the gate does.
        var (other, _, _, otherStore) = Start();
        Publish(otherStore, Cloudflare(TrustedProxyMode.Generic));
        var inert = Assert.Single(other.Read().Hosts, host => host.Name == "a.test");
        Assert.Equal((OriginLockdownMode.Off, false), (inert.OriginLockdown, inert.AuthenticatedOriginPulls));
    }

    [Fact]
    public void OnlyEnabledHostsAreListedByNameWhateverTheirOrder()
    {
        var (service, _, _, store) = Start();
        Publish(store, Model(
            Host(1, "zeta.test"),
            Host(2, "Alpha.test"),
            Host(3, "off.test") with { Enabled = false },
            Host(4, "mid.test")));

        var names = service.Read().Hosts.Select(host => host.Name);

        Assert.Equal(["Alpha.test", "mid.test", "zeta.test"], names);
    }

    [Fact]
    public void ARedirectHostCarriesItsRobotsModeAndNoRouteSettings()
    {
        var (service, _, _, store) = Start();
        Publish(store, Model(new HostSpec
        {
            Id = Id<HostId>(1),
            Names = ["old.test"],
            Https = false,
            Kind = HostKind.Redirect,
            Redirect = new RedirectSpec { Target = "https://new.test" },
            Robots = new HostRobotsSpec { Mode = RobotsMode.DisallowAll },
        }));

        var host = Assert.Single(service.Read().Hosts);

        Assert.Equal(RobotsMode.DisallowAll, host.Robots);
        Assert.False(host.ProofOfWork.Enabled);
        Assert.Equal(0, host.ProofOfWork.RouteOverrides);
        Assert.Null(host.RateLimit.Policy);
        Assert.Empty(host.RateLimit.Overrides);
    }

    private static (SecurityOverviewService Service, ProofOfWorkRuntime ProofOfWork, RateLimitRuntime RateLimits, SnapshotStore Store) Start(FakeTime? time = null)
    {
        ProofOfWorkRuntime proofOfWork = new();
        RateLimitRuntime rateLimits = new(new RateLimitTable(64));
        SnapshotStore store = new();
        return (new SecurityOverviewService(store, proofOfWork, rateLimits, time ?? new FakeTime(DateTimeOffset.UnixEpoch)), proofOfWork, rateLimits, store);
    }

    private static HostSpec Host(int n, string name) => new() { Id = Id<HostId>(n), Names = [name], Https = false, Routes = [new RouteSpec { Id = Id<RouteId>(1000 + n), Upstream = App }] };

    private static RouteSpec Route(int n, string path) => new() { Id = Id<RouteId>(n), Upstream = App, Path = path };

    private static ProxyConfigModel Model(params HostSpec[] hosts) => new()
    {
        Upstreams = [new UpstreamSpec { Id = App, Destinations = [new DestinationSpec(Id<DestinationId>(1), "http://127.0.0.1:1")] }],
        RateLimitPolicies =
        [
            new RateLimitPolicySpec { Id = Standard, Name = "standard", Parameters = new RateLimitParameters(10, 20) },
            new RateLimitPolicySpec { Id = Strict, Name = "strict", Parameters = new RateLimitParameters(1, 2) },
        ],
        AccessLists =
        [
            new AccessListSpec
            {
                Id = Office,
                Name = "office",
                Allow = ["10.0.0.0/8", "10.1.0.0/16", "192.168.0.0/16"],
                Realm = "office",
                Users = [new AccessListUserSpec("alice", SampleHash), new AccessListUserSpec("bob", SampleHash)],
            },
            new AccessListSpec { Id = Lan, Name = "lan", Allow = ["192.168.0.0/16"], Realm = "lan" },
        ],
        Hosts = hosts,
    };

    private static void Publish(SnapshotStore store, ProxyConfigModel model)
    {
        var result = SnapshotCompiler.Compile(
            model,
            new SnapshotCompilerOptions { RateLimits = new RateLimitRuntime(new RateLimitTable(64)), ProofOfWork = new ProofOfWorkRuntime() });
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
        var snapshot = result.Snapshot!;
        store.Publish(snapshot.Bind(1, [.. snapshot.RouteIdBySlot.Select(_ => new Endpoint(null, null, "route"))]));
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
