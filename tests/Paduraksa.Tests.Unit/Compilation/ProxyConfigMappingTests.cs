using Paduraksa.Application.Certificates;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;

using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Compilation;

public sealed class ProxyConfigMappingTests
{
    private static readonly ConfigRevisionId Revision = Id<ConfigRevisionId>(7);
    private static readonly IpAllowListSettings Lan = new(["192.168.0.0/16"]);

    [Fact]
    public void ARepresentativeDocumentMapsToTheExpectedModel()
    {
        ConfigDocument document = new(
            Settings: new GlobalSettings(
                Defaults: new HostSettings { ForceHttps = true, Tls = new TlsSettings { MinimumVersion = TlsMinimumVersion.Tls13 } },
                UnknownHost: UnknownHostBehaviour.MisdirectedRequest,
                DefaultCertificateId: Id<CertificateId>(1),
                TrustedProxies: new TrustedProxySettings(TrustedProxyMode.Generic, [" 10.0.0.0/8 ", "192.168.1.1/32"], Header: "X-Real-IP")),
            Profiles: [new ProfileConfig(LanProfile, "LAN only", Settings: new HostSettings { Tls = new TlsSettings { Http3 = false }, IpAllowList = Lan })],
            Upstreams:
            [
                new UpstreamConfig(
                    AppUpstream,
                    "app",
                    Destinations: [new DestinationConfig(AppDestination, " http://app:8080 "), new DestinationConfig(Id<DestinationId>(2), "http://spare:8080", Enabled: false)],
                    LoadBalancing: LoadBalancingPolicy.RoundRobin,
                    SessionAffinity: true,
                    PassiveHealth: new PassiveHealthConfig { ReactivationPeriod = TimeSpan.FromSeconds(30) },
                    ActiveHealth: new ActiveHealthConfig("/healthz")),
                new UpstreamConfig(Id<UpstreamId>(2), "api", Destinations: [new DestinationConfig(Id<DestinationId>(3), "http://api:9000")], MaxConnectionsPerServer: 16),
            ],
            Hosts:
            [
                new HostConfig(
                    AppHost,
                    ["App.Example.com.", "*.app.example.com"],
                    ProfileId: LanProfile,
                    Certificate: new CertificateBinding { Mode = CertificateBindingMode.Specific, CertificateId = Id<CertificateId>(2) },
                    Routes:
                    [
                        new RouteConfig(RootRoute, AppUpstream, Order: 10),
                        new RouteConfig(ApiRoute, Id<UpstreamId>(2), Path: "/api", Settings: new RouteSettings { IpAllowList = new IpAllowListSettings([]) }),
                        new RouteConfig(
                            Id<RouteId>(3),
                            AppUpstream,
                            Match: RouteMatchKind.Exact,
                            Path: "/admin",
                            Settings: new RouteSettings { IpAllowList = new IpAllowListSettings(["192.168.1.0/24"], DropDenied: true) }),
                    ]),
                new HostConfig(
                    Id<HostId>(2),
                    ["plain.example.com"],
                    Certificate: new CertificateBinding { Mode = CertificateBindingMode.None },
                    Settings: new HostSettings { ForceHttps = false },
                    Routes: [new RouteConfig(Id<RouteId>(4), AppUpstream)]),
                new HostConfig(
                    Id<HostId>(3),
                    ["old.example.com"],
                    Kind: HostKind.Redirect,
                    ProfileId: LanProfile,
                    Redirect: new RedirectConfig(" https://app.example.com ", 308)),
                new HostConfig(Id<HostId>(4), ["soon.example.com"], Kind: HostKind.Respond, Enabled: false, Respond: new RespondConfig(503, "soon")),
            ]);

        HostTlsSpec lanTls = new() { MinimumVersion = TlsMinimumVersion.Tls13, Http3 = false };
        ProxyConfigModel expected = new()
        {
            Revision = Revision,
            Hosts =
            [
                new HostSpec
                {
                    Id = AppHost,
                    Names = ["*.app.example.com", "app.example.com"],
                    ForceHttps = true,
                    Certificate = Id<CertificateId>(2),
                    Tls = lanTls,
                    // Canonical (evaluation) order. The routes' lists differ, so each route carries its own.
                    Routes =
                    [
                        new RouteSpec { Id = ApiRoute, Path = "/api", Upstream = Id<UpstreamId>(2), Policies = [] },
                        new RouteSpec
                        {
                            Id = Id<RouteId>(3),
                            Match = RouteMatchKind.Exact,
                            Path = "/admin",
                            Upstream = AppUpstream,
                            Policies = [new IpAllowListPolicySpec(["192.168.1.0/24"], DropDenied: true)],
                        },
                        new RouteSpec { Id = RootRoute, Order = 10, Upstream = AppUpstream, Policies = [new IpAllowListPolicySpec(["192.168.0.0/16"])] },
                    ],
                    Policies = [],
                },
                new HostSpec
                {
                    Id = Id<HostId>(2),
                    Names = ["plain.example.com"],
                    Https = false,
                    Tls = new HostTlsSpec { MinimumVersion = TlsMinimumVersion.Tls13 },
                    Routes = [new RouteSpec { Id = Id<RouteId>(4), Upstream = AppUpstream }],
                },
                new HostSpec
                {
                    Id = Id<HostId>(3),
                    Names = ["old.example.com"],
                    Kind = HostKind.Redirect,
                    ForceHttps = true,
                    Tls = lanTls,
                    Policies = [new IpAllowListPolicySpec(["192.168.0.0/16"])],
                    Redirect = new RedirectSpec { Target = "https://app.example.com", StatusCode = 308 },
                },
                new HostSpec
                {
                    Id = Id<HostId>(4),
                    Names = ["soon.example.com"],
                    Kind = HostKind.Respond,
                    Enabled = false,
                    ForceHttps = true,
                    Tls = new HostTlsSpec { MinimumVersion = TlsMinimumVersion.Tls13 },
                    Respond = new RespondSpec { StatusCode = 503, Body = "soon" },
                },
            ],
            Upstreams =
            [
                new UpstreamSpec
                {
                    Id = AppUpstream,
                    Destinations = [new DestinationSpec(AppDestination, "http://app:8080")],
                    LoadBalancing = "RoundRobin",
                    SessionAffinity = true,
                    PassiveHealth = true,
                    ReactivationPeriod = TimeSpan.FromSeconds(30),
                    ActiveHealth = new ActiveHealthSpec { Path = "/healthz", Interval = TimeSpan.FromSeconds(15), Timeout = TimeSpan.FromSeconds(5) },
                },
                new UpstreamSpec
                {
                    Id = Id<UpstreamId>(2),
                    Destinations = [new DestinationSpec(Id<DestinationId>(3), "http://api:9000")],
                    MaxConnectionsPerServer = 16,
                },
            ],
            Defaults = new DefaultsSpec
            {
                UnknownHost = UnknownHostBehaviour.MisdirectedRequest,
                Certificate = Id<CertificateId>(1),
                Tls = new HostTlsSpec { MinimumVersion = TlsMinimumVersion.Tls13 },
                TrustedProxies = new TrustedProxySpec
                {
                    Mode = TrustedProxyMode.Generic,
                    Proxies = ["10.0.0.0/8", "192.168.1.1"],
                    Header = "x-real-ip",
                },
            },
        };

        Assert.Empty(ConfigValidator.Validate(document));
        using AnyCertificate certificates = new();
        var mapped = ProxyConfigMapping.Map(document, Revision, certificates.InventoryFor(document));
        Assert.Empty(mapped.Errors);
        Assert.Equivalent(expected, mapped.Model, strict: true);
    }

    [Fact]
    public void TwoSpellingsOfOneConfigMapToTheSameModel()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var document = Random(seed);
            Assert.Equivalent(ProxyConfigMapping.Map(document, Revision, CertificateInventory.Empty).Model, ProxyConfigMapping.Map(Respelled(document, seed), Revision, CertificateInventory.Empty).Model, strict: true);
        }
    }

    [Fact]
    public void WithoutRouteOverridesTheListGuardsTheWholeHost()
    {
        // Also when a route "overrides" it with the same list.
        var host = MapOnlyHost(
            new HostSettings { IpAllowList = Lan },
            new RouteSettings(),
            new RouteSettings { IpAllowList = new IpAllowListSettings([" 192.168.0.0/16", "192.168.0.0/16"]) });

        Assert.Equal(["192.168.0.0/16"], Assert.IsType<IpAllowListPolicySpec>(Assert.Single(host.Policies)).Addresses);
        Assert.All(host.Routes, r => Assert.Empty(r.Policies));
    }

    [Fact]
    public void RoutesThatAllOverrideAlikeGuardTheWholeHost()
    {
        var host = MapOnlyHost(null, new RouteSettings { IpAllowList = Lan }, new RouteSettings { IpAllowList = Lan });

        Assert.Equal(Lan.Addresses, Assert.IsType<IpAllowListPolicySpec>(Assert.Single(host.Policies)).Addresses);
        Assert.All(host.Routes, r => Assert.Empty(r.Policies));
    }

    [Fact]
    public void ARouteCanTightenTheHostsList()
    {
        var host = MapOnlyHost(
            new HostSettings { IpAllowList = Lan },
            new RouteSettings { IpAllowList = new IpAllowListSettings(["10.0.0.0/8"], DropDenied: true) },
            new RouteSettings { IpAllowList = new IpAllowListSettings(["192.168.1.7"], DropDenied: true) });

        // Each route carries its own list, so an override replaces the host's instead of stacking under it; the host
        // chain carries their union, so a client no route admits can't tell the paths apart. Every route drops.
        Assert.Equal(new IpAllowListPolicySpec(["10.0.0.0/8", "192.168.1.7"], DropDenied: true), Assert.IsType<IpAllowListPolicySpec>(Assert.Single(host.Policies)), AllowListComparer.Instance);
        Assert.Equal(new IpAllowListPolicySpec(["10.0.0.0/8"], DropDenied: true), Assert.IsType<IpAllowListPolicySpec>(Assert.Single(host.Routes[0].Policies)), AllowListComparer.Instance);
        Assert.Equal(new IpAllowListPolicySpec(["192.168.1.7"], DropDenied: true), Assert.IsType<IpAllowListPolicySpec>(Assert.Single(host.Routes[1].Policies)), AllowListComparer.Instance);
    }

    [Fact]
    public void TheUnionDropsOnlyWhenEveryRouteDrops()
    {
        var host = MapOnlyHost(
            new HostSettings { IpAllowList = Lan },
            new RouteSettings(),
            new RouteSettings { IpAllowList = new IpAllowListSettings(["192.168.1.7", "10.0.0.1"], DropDenied: true) });

        Assert.Equal(new IpAllowListPolicySpec(["10.0.0.1", "192.168.0.0/16", "192.168.1.7"]), Assert.IsType<IpAllowListPolicySpec>(Assert.Single(host.Policies)), AllowListComparer.Instance);
        Assert.Equal(new IpAllowListPolicySpec(Lan.Addresses), Assert.IsType<IpAllowListPolicySpec>(Assert.Single(host.Routes[0].Policies)), AllowListComparer.Instance);
        Assert.Equal(new IpAllowListPolicySpec(["10.0.0.1", "192.168.1.7"], DropDenied: true), Assert.IsType<IpAllowListPolicySpec>(Assert.Single(host.Routes[1].Policies)), AllowListComparer.Instance);
    }

    [Fact]
    public void ARouteCanLiftTheHostsList()
    {
        var host = MapOnlyHost(new HostSettings { IpAllowList = Lan }, new RouteSettings(), new RouteSettings { IpAllowList = new IpAllowListSettings([]) });

        // Anyone may reach the lifted route, so the host chain hides nothing.
        Assert.Empty(host.Policies);
        Assert.Equal(Lan.Addresses, Assert.IsType<IpAllowListPolicySpec>(Assert.Single(host.Routes[0].Policies)).Addresses);
        Assert.Empty(host.Routes[1].Policies);
    }

    [Fact]
    public void ARouteCanRestrictAnUnrestrictedHost()
    {
        var host = MapOnlyHost(null, new RouteSettings(), new RouteSettings { IpAllowList = Lan });

        Assert.Empty(host.Policies);
        Assert.Empty(host.Routes[0].Policies);
        Assert.Equal(Lan.Addresses, Assert.IsType<IpAllowListPolicySpec>(Assert.Single(host.Routes[1].Policies)).Addresses);
    }

    [Fact]
    public void AnAdminHostTakesTheSameHsts()
    {
        var document = Document(
            defaults: new HostSettings { Hsts = new HstsSettings { Enabled = true, MaxAge = 86_400 } },
            profile: null,
            host: null);
        var admin = document with
        {
            Hosts = [document.Hosts[0] with { Kind = HostKind.Admin, Routes = [], Settings = new HostSettings { Hsts = new HstsSettings { Preload = false } } }],
        };

        var host = Assert.Single(ProxyConfigMapping.Map(admin, Revision, CertificateInventory.Empty).Model.Hosts);

        Assert.Equal(HostKind.Admin, host.Kind);
        Assert.Equal(new HostHstsSpec { Enabled = true, MaxAgeSeconds = 86_400, IncludeSubDomains = false, Preload = false }, host.Hsts);
    }

    [Fact]
    public void AnEmptyListLiftsAnInheritedOne()
    {
        var document = Document(
            defaults: new HostSettings { IpAllowList = Lan },
            profile: new HostSettings(),
            host: new HostSettings { IpAllowList = new IpAllowListSettings([]) });

        Assert.Empty(Assert.Single(ProxyConfigMapping.Map(document, Revision, CertificateInventory.Empty).Model.Hosts).Policies);
    }

    [Fact]
    public void AMissingProfileAddsNoLayer()
    {
        var document = Document(new HostSettings { ForceHttps = true }, new HostSettings { ForceHttps = false }, null);
        document = document with { Hosts = [document.Hosts[0] with { ProfileId = Id<ProfileId>(99) }] };

        Assert.True(Assert.Single(ProxyConfigMapping.Map(document, Revision, CertificateInventory.Empty).Model.Hosts).ForceHttps);
    }

    [Fact]
    public void CertificateBindings()
    {
        using AnyCertificate certificates = new();
        HostSpec Bound(CertificateBinding binding)
        {
            var document = Document(null, null, null);
            var bound = document with { Hosts = [document.Hosts[0] with { Certificate = binding }] };
            return Assert.Single(ProxyConfigMapping.Map(bound, Revision, certificates.InventoryFor(bound)).Model.Hosts);
        }

        var auto = Bound(new CertificateBinding());
        var specific = Bound(new CertificateBinding { Mode = CertificateBindingMode.Specific, CertificateId = Id<CertificateId>(5) });
        var none = Bound(new CertificateBinding { Mode = CertificateBindingMode.None });
        var missing = Bound(new CertificateBinding { Mode = CertificateBindingMode.Specific });

        Assert.Equal((true, (CertificateId?)null), (auto.Https, auto.Certificate));
        Assert.Equal((true, (CertificateId?)Id<CertificateId>(5)), (specific.Https, specific.Certificate));
        Assert.Equal((false, (CertificateId?)null), (none.Https, none.Certificate));
        Assert.Equal((true, (CertificateId?)CertificateId.Empty), (missing.Https, missing.Certificate)); // the compiler reports it
    }

    [Fact]
    public void ValuesTheModelCantCarryAreReportedByTheMapping()
    {
        var document = Valid();
        var changed = document with
        {
            Upstreams = [document.Upstreams[0] with { LoadBalancing = (LoadBalancingPolicy)9 }],
            Hosts =
            [
                .. document.Hosts.Select(h => h.Id == AppHost ? h with { Certificate = new CertificateBinding { Mode = (CertificateBindingMode)9 } }
                    : h.Id == RespondHost ? h with { Respond = h.Respond! with { ContentType = null!, Body = null! } }
                    : h),
            ],
        };

        var mapped = ProxyConfigMapping.Map(changed, Revision, CertificateInventory.Empty);

        Assert.Equal(
            [
                CompileErrorSource.Host(AppHost),
                CompileErrorSource.Host(RespondHost),
                CompileErrorSource.Upstream(AppUpstream),
            ],
            mapped.Errors.Select(e => e.Source));
        Assert.Null(Assert.Single(mapped.Model.Upstreams).LoadBalancing);
        Assert.Equal(string.Empty, mapped.Model.Hosts.Single(h => h.Id == RespondHost).Respond!.Body);
    }

    [Fact]
    public void LoadBalancingPoliciesMapToYarpNames()
    {
        foreach (var policy in Enum.GetValues<LoadBalancingPolicy>())
        {
            var document = Valid();
            var changed = document with { Upstreams = [document.Upstreams[0] with { LoadBalancing = policy }] };

            Assert.Equal(policy.ToString(), Assert.Single(ProxyConfigMapping.Map(changed, Revision, CertificateInventory.Empty).Model.Upstreams).LoadBalancing);
        }

        Assert.Null(Assert.Single(ProxyConfigMapping.Map(Valid(), Revision, CertificateInventory.Empty).Model.Upstreams).LoadBalancing);
    }

    [Fact]
    public void AnAdminHostsExplicitForceHttpsFalseWarnsThatItIsIgnored()
    {
        ConfigDocument document = new(
            Hosts: [new HostConfig(Id<HostId>(50), ["admin.example.com"], Kind: HostKind.Admin, Settings: new HostSettings { ForceHttps = false })]);

        var mapped = ProxyConfigMapping.Map(document, Revision, CertificateInventory.Empty);

        var warning = Assert.Single(mapped.Warnings, w => w.Code == ConfigWarningCodes.AdminForceHttpsIgnored);
        Assert.Equal("/settings/forceHttps", warning.Member);
        Assert.Equal(CompileErrorSource.Host(Id<HostId>(50)), warning.Source);
        Assert.Contains("always served over HTTPS", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PinnedTrustCarriesNormalisedFingerprintsAndAnyTrustWarnsOnlyForHttps()
    {
        static UpstreamConfig Upstream(int id, string address, UpstreamTlsConfig tls) =>
            new(Id<UpstreamId>(id), $"u{id}", Destinations: [new DestinationConfig(Id<DestinationId>(id), address)], Tls: tls);
        var colons = string.Join(':', Enumerable.Repeat("AB", 32));
        ConfigDocument document = new(Upstreams:
        [
            Upstream(1, "https://nas:8443", new UpstreamTlsConfig(UpstreamTlsTrust.Pinned, [colons, new string('b', 64), new string('B', 64)])),
            Upstream(2, "https://router", new UpstreamTlsConfig(UpstreamTlsTrust.Any)),
            Upstream(3, "http://plain", new UpstreamTlsConfig(UpstreamTlsTrust.Any)),
        ]);

        var mapped = ProxyConfigMapping.Map(document, Revision, CertificateInventory.Empty);

        var pinned = Assert.Single(mapped.Model.Upstreams, u => u.Id == Id<UpstreamId>(1));
        Assert.Equal(UpstreamTlsTrust.Pinned, pinned.TlsTrust);
        // Colons dropped, lowercased, and the two spellings of one fingerprint kept once.
        Assert.Equal([string.Concat(Enumerable.Repeat("ab", 32)), new string('b', 64)], pinned.PinnedSha256.Order(StringComparer.Ordinal));
        var warning = Assert.Single(mapped.Warnings, w => w.Code == ConfigWarningCodes.UpstreamTlsUnverified);
        Assert.Equal(CompileErrorSource.Upstream(Id<UpstreamId>(2)), warning.Source);
        Assert.Equal("/tls/trust", warning.Member);
    }

    // One proxy host with two routes ("/" and "/b") under a profile-less, default-less document.
    private static HostSpec MapOnlyHost(HostSettings? host, RouteSettings first, RouteSettings second)
    {
        var document = Document(null, null, host);
        var config = document.Hosts[0] with
        {
            Routes = [new RouteConfig(RootRoute, AppUpstream, Settings: first), new RouteConfig(ApiRoute, AppUpstream, Path: "/b", Settings: second)],
        };
        return Assert.Single(ProxyConfigMapping.Map(document with { Hosts = [config] }, Revision, CertificateInventory.Empty).Model.Hosts);
    }

    private static ConfigDocument Document(HostSettings? defaults, HostSettings? profile, HostSettings? host) => new(
        Settings: new GlobalSettings(Defaults: defaults),
        Profiles: profile is null ? [] : [new ProfileConfig(LanProfile, "profile", Settings: profile)],
        Upstreams: [new UpstreamConfig(AppUpstream, "app", Destinations: [new DestinationConfig(AppDestination, "http://app")])],
        Hosts: [new HostConfig(AppHost, ["app.example.com"], ProfileId: profile is null ? null : LanProfile, Settings: host, Routes: [new RouteConfig(RootRoute, AppUpstream)])]);

    private sealed class AllowListComparer : IEqualityComparer<IpAllowListPolicySpec>
    {
        public static readonly AllowListComparer Instance = new();

        public bool Equals(IpAllowListPolicySpec? x, IpAllowListPolicySpec? y) =>
            x is not null && y is not null && x.DropDenied == y.DropDenied && x.Addresses.SequenceEqual(y.Addresses, StringComparer.Ordinal);

        public int GetHashCode(IpAllowListPolicySpec obj) => obj.DropDenied.GetHashCode();
    }
}
