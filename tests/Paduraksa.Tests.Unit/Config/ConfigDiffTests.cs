using System.Text.Json;

using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Config;

public sealed class ConfigDiffTests
{
    [Fact]
    public void EquivalentDocumentsHaveNoChanges()
    {
        for (var seed = 0; seed < 100; seed++)
        {
            var document = Random(seed);

            Assert.Empty(ConfigDiff.Compare(document, document));
            Assert.Empty(ConfigDiff.Compare(document, Respelled(document, seed)));
        }
    }

    [Fact]
    public void VersionsAloneAreNoChange()
    {
        var document = Valid();
        var bumped = document with
        {
            Settings = document.Settings with { Version = 7 },
            Profiles = [.. document.Profiles.Select(p => p with { Version = p.Version + 1 })],
            Upstreams = [.. document.Upstreams.Select(u => u with { Version = u.Version + 1 })],
            Hosts = [.. document.Hosts.Select(h => h with { Version = h.Version + 1 })],
        };

        Assert.Empty(ConfigDiff.Compare(document, bumped));
    }

    [Fact]
    public void ListsChangedSettingsFields()
    {
        var document = Valid();
        var changed = document with
        {
            Settings = document.Settings with
            {
                Defaults = new HostSettings
                {
                    ForceHttps = false,
                    Tls = new TlsSettings { Http3 = false },
                    Robots = new RobotsSettings { Mode = RobotsMode.BlockAiCrawlers },
                },
                TrustedProxies = document.Settings.TrustedProxies with { Proxies = ["10.0.0.0/8", "172.16.0.0/12"] },
            },
        };

        var change = Assert.IsType<SettingsChange>(Assert.Single(ConfigDiff.Compare(document, changed)));

        Assert.Equal(ConfigChangeKind.Modified, change.Kind);
        Assert.Equal("/settings", change.Path);
        Assert.Equal(["/settings/defaults/forceHttps", "/settings/defaults/tls", "/settings/defaults/robots", "/settings/trustedProxies/proxies"], change.Fields);
    }

    [Fact]
    public void ListsAddedRemovedAndModifiedObjectsInOrder()
    {
        var document = Valid();
        ProfileConfig media = new(Id<ProfileId>(2), "Media");
        UpstreamConfig other = new(Id<UpstreamId>(2), "other", Destinations: [new DestinationConfig(Id<DestinationId>(2), "http://other")]);
        var changed = document with
        {
            Profiles = [media],
            Upstreams = [.. document.Upstreams, other],
            Hosts =
            [
                .. document.Hosts
                    .Where(h => h.Id != RespondHost)
                    .Select(h => h.Id == RedirectHost ? h with { Names = ["old.example.net"], Redirect = h.Redirect! with { StatusCode = 308 } } : h),
            ],
        };

        var changes = ConfigDiff.Compare(document, changed);

        Assert.Equal(
            [
                new ProfileChange(ConfigChangeKind.Removed, LanProfile, "/profiles/0", []),
                new ProfileChange(ConfigChangeKind.Added, media.Id, "/profiles/0", []),
                new UpstreamChange(ConfigChangeKind.Added, other.Id, "/upstreams/1", []),
                new HostChange(ConfigChangeKind.Modified, RedirectHost, "/hosts/1", ["/hosts/1/names", "/hosts/1/redirect/statusCode"]),
                new HostChange(ConfigChangeKind.Removed, RespondHost, "/hosts/2", []),
            ],
            changes,
            ChangeComparer.Instance);
    }

    [Fact]
    public void ListsRateLimitPolicyChanges()
    {
        var document = Valid();
        var edited = document.RateLimitPolicies[0] with { Requests = 120 };
        RateLimitPolicyConfig added = new(Id<RateLimitPolicyId>(2), "burst");
        var changed = document with { RateLimitPolicies = [edited, added] };

        Assert.Equal(
            [
                new RateLimitPolicyChange(ConfigChangeKind.Modified, ApiPolicy, "/rateLimitPolicies/0", ["/rateLimitPolicies/0/requests"]),
                new RateLimitPolicyChange(ConfigChangeKind.Added, added.Id, "/rateLimitPolicies/1", []),
            ],
            ConfigDiff.Compare(document, changed),
            ChangeComparer.Instance);
    }

    [Fact]
    public void ListsChildrenUnderTheirParent()
    {
        var document = Valid();
        var added = Id<DestinationId>(2);
        var changed = document with
        {
            Upstreams =
            [
                document.Upstreams[0] with
                {
                    Destinations = [.. document.Upstreams[0].Destinations, new DestinationConfig(added, "http://app2:8080")],
                },
            ],
            Hosts =
            [
                .. document.Hosts.Select(h => h.Id != AppHost ? h : h with
                {
                    Tags = ["media", "lan"],
                    Routes = [h.Routes[0] with { Path = "/app", Settings = new RouteSettings { IpAllowList = new IpAllowListSettings(["::1"]) } }],
                }),
            ],
        };

        var changes = ConfigDiff.Compare(document, changed);

        // Canonical routes before: 0 = /api (order 0), 1 = / (order 10, now /app). The host's own change is its tags.
        Assert.Equal(
            [
                new DestinationChange(ConfigChangeKind.Added, AppUpstream, added, "/upstreams/0/destinations/1", []),
                new HostChange(ConfigChangeKind.Modified, AppHost, "/hosts/0", ["/hosts/0/tags"]),
                new RouteChange(
                    ConfigChangeKind.Modified,
                    AppHost,
                    RootRoute,
                    "/hosts/0/routes/0",
                    ["/hosts/0/routes/0/path", "/hosts/0/routes/0/settings/ipAllowList"]),
                new RouteChange(ConfigChangeKind.Removed, AppHost, ApiRoute, "/hosts/0/routes/0", []),
            ],
            changes,
            ChangeComparer.Instance);
    }

    [Fact]
    public void AMovedRouteLeavesOneHostAndJoinsAnother()
    {
        var document = Valid();
        var api = document.Hosts[0].Routes.Single(r => r.Id == ApiRoute);
        HostConfig respond = new(Id<HostId>(9), ["api.example.com"], Kind: HostKind.Respond, Respond: new RespondConfig(404));
        var proxy = respond with { Kind = HostKind.Proxy, Respond = null, Routes = [api] };
        HostConfig[] moved =
        [
            .. document.Hosts.Select(h => h.Id == AppHost ? h with { Routes = [.. h.Routes.Where(r => r.Id != ApiRoute)] } : h),
            proxy,
        ];

        // Into a new host: the added host carries the route.
        Assert.Equal(
            [
                new RouteChange(ConfigChangeKind.Removed, AppHost, ApiRoute, "/hosts/0/routes/0", []),
                new HostChange(ConfigChangeKind.Added, proxy.Id, "/hosts/3", []),
            ],
            ConfigDiff.Compare(document, document with { Hosts = moved }),
            ChangeComparer.Instance);

        // Into an existing host: removed from one, added to the other.
        Assert.Equal(
            [
                new RouteChange(ConfigChangeKind.Removed, AppHost, ApiRoute, "/hosts/0/routes/0", []),
                new HostChange(ConfigChangeKind.Modified, proxy.Id, "/hosts/3", ["/hosts/3/kind", "/hosts/3/respond"]),
                new RouteChange(ConfigChangeKind.Added, proxy.Id, ApiRoute, "/hosts/3/routes/0", []),
            ],
            ConfigDiff.Compare(document with { Hosts = [.. document.Hosts, respond] }, document with { Hosts = moved }),
            ChangeComparer.Instance);
    }

    [Fact]
    public void ReorderingRoutesOfEqualSpecificityIsAHostChange()
    {
        RouteConfig a = new(ApiRoute, AppUpstream, Path: "/a");
        RouteConfig b = new(RootRoute, AppUpstream, Path: "/b");
        RouteConfig extra = new(Id<RouteId>(3), AppUpstream, Path: "/extra");
        static ConfigDocument Document(params RouteConfig[] routes) =>
            new(ConfigDocument.V1, Hosts: [new HostConfig(AppHost, ["a.example.com"], Tags: ["x"], Routes: routes)]);

        // Equal kind, path length and Order: the listed order is the last tiebreaker, so swapping them is a change.
        Assert.Equal(
            [new HostChange(ConfigChangeKind.Modified, AppHost, "/hosts/0", ["/hosts/0/routes"])],
            ConfigDiff.Compare(Document(a, b), Document(b, a)),
            ChangeComparer.Instance);

        // Different specificity: the canonical order is the evaluation order, so the listing is no change.
        RouteConfig api = new(ApiRoute, AppUpstream, Path: "/api");
        RouteConfig root = new(RootRoute, AppUpstream);
        Assert.Empty(ConfigDiff.Compare(Document(api, root), Document(root, api)));

        // Adding a longer route puts it first in the canonical list.
        Assert.Equal(
            [new RouteChange(ConfigChangeKind.Added, AppHost, extra.Id, "/hosts/0/routes/0", [])],
            ConfigDiff.Compare(Document(api, root), Document(api, extra, root)),
            ChangeComparer.Instance);

        // Order no longer moves a route past a different path; it is only the route's own field.
        Assert.Equal(
            [new RouteChange(ConfigChangeKind.Modified, AppHost, ApiRoute, "/hosts/0/routes/0", ["/hosts/0/routes/0/order"])],
            ConfigDiff.Compare(Document(api, root), Document(api with { Order = 5 }, root)),
            ChangeComparer.Instance);
    }

    [Fact]
    public void ReportsAnApiVersionChange()
    {
        var document = Valid();

        Assert.Equal(
            [new DocumentChange(ConfigChangeKind.Modified, string.Empty, ["/apiVersion"])],
            ConfigDiff.Compare(document, document with { ApiVersion = "paduraksa.dev/v2" }),
            ChangeComparer.Instance);
    }

    [Fact]
    public void ComparesInvalidDocumentsWithoutThrowing()
    {
        var document = Valid();
        var invalid = document with
        {
            Settings = document.Settings with { UnknownHost = (UnknownHostBehaviour)9 },
            Profiles = [.. document.Profiles, .. document.Profiles, null!],
            Upstreams = [document.Upstreams[0] with { Id = default, Destinations = [null!], LoadBalancing = (LoadBalancingPolicy)42 }],
            Hosts =
            [
                .. document.Hosts.Select(h => h with { Names = ["-bad-", null!], Kind = (HostKind)7, Routes = [.. h.Routes, .. h.Routes] }),
                null!,
            ],
        };
        Assert.NotEmpty(ConfigValidator.Validate(invalid));

        Assert.NotEmpty(ConfigDiff.Compare(document, invalid));
        Assert.NotEmpty(ConfigDiff.Compare(invalid, document));
        Assert.Empty(ConfigDiff.Compare(invalid, invalid));
    }

    [Fact]
    public void ANestedObjectSetOnOneSideIsOneField()
    {
        var document = Valid();
        var changed = document with
        {
            Upstreams = [document.Upstreams[0] with { ActiveHealth = null, PassiveHealth = new PassiveHealthConfig() }],
        };

        var change = Assert.IsType<UpstreamChange>(Assert.Single(ConfigDiff.Compare(document, changed)));

        Assert.Equal(["/upstreams/0/passiveHealth", "/upstreams/0/activeHealth"], change.Fields);
    }

    [Fact]
    public void ChangeListsRoundTripThroughJson()
    {
        IReadOnlyList<ConfigChange> changes =
        [
            new SettingsChange(ConfigChangeKind.Modified, "/settings", ["/settings/unknownHost"]),
            new DestinationChange(ConfigChangeKind.Added, AppUpstream, AppDestination, "/upstreams/0/destinations/0", []),
            new RouteChange(ConfigChangeKind.Removed, AppHost, RootRoute, "/hosts/0/routes/1", []),
        ];

        var json = JsonSerializer.Serialize(changes, ConfigJsonContext.Default.IReadOnlyListConfigChange);
        IReadOnlyList<ConfigChange> read = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.IReadOnlyListConfigChange)!;

        Assert.StartsWith("""[{"object":"settings","kind":"modified","path":"/settings","fields":["/settings/unknownHost"]}""", json, StringComparison.Ordinal);
        Assert.Contains($"\"upstreamId\":\"{AppUpstream}\"", json, StringComparison.Ordinal);
        Assert.Equal(changes, read, ChangeComparer.Instance);
    }

    /// <summary>Records with list members compare those by reference; compare the lists by content instead.</summary>
    private sealed class ChangeComparer : IEqualityComparer<ConfigChange>
    {
        public static readonly ChangeComparer Instance = new();

        public bool Equals(ConfigChange? x, ConfigChange? y) =>
            x is not null && y is not null
            && x.GetType() == y.GetType()
            && x.Fields.SequenceEqual(y.Fields)
            && (x with { Fields = [] }).Equals(y with { Fields = [] });

        public int GetHashCode(ConfigChange obj) => obj.Path.GetHashCode(StringComparison.Ordinal);
    }
}
