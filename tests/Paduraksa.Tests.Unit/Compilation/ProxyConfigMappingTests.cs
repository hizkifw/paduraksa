using Paduraksa.Application.Certificates;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.Config;

using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Compilation;

public sealed class ProxyConfigMappingTests
{
    private static readonly ConfigRevisionId Revision = Id<ConfigRevisionId>(7);
    private static readonly AccessListConfig Lan = List(1, "lan", allow: ["192.168.0.0/16"]);
    private static readonly AccessListConfig Ten = List(2, "ten", allow: ["10.0.0.0/8"], drop: true);
    private static readonly AccessListConfig Seven = List(3, "seven", allow: ["192.168.1.7"], drop: true);
    private static readonly AccessListConfig Passwords = List(4, "passwords", users: true);
    private static readonly AccessListConfig LanOrPassword = List(5, "lan-or-password", allow: ["192.168.0.0/16"], users: true, satisfy: AccessListSatisfy.Any);
    private static readonly AccessListConfig LanAndPassword = List(6, "lan-and-password", allow: ["192.168.0.0/16"], users: true);
    private static readonly AccessListSettings Off = new() { Enabled = false };
    private static readonly AccessListConfig[] AllLists = [Lan, Ten, Seven, Passwords, LanOrPassword, LanAndPassword];

    private static AccessListConfig List(int id, string name, string[]? allow = null, bool drop = false, bool users = false, AccessListSatisfy satisfy = AccessListSatisfy.All) => new(
        Id<AccessListId>(id),
        name,
        Ip: allow is null ? null : new AccessListIp(allow, DropDenied: drop),
        BasicAuth: users ? new AccessListBasicAuth(Users: [new AccessListUserConfig("alice", Id<PasswordCredentialId>(1))]) : null,
        Satisfy: satisfy);

    private static AccessListSettings Use(AccessListConfig list) => new() { AccessListId = list.Id };

    private static AccessListPolicySpec Full(AccessListConfig list) => new(list.Id);

    private static AccessListPolicySpec AddressOnly(AccessListConfig list) => new(list.Id, AccessListStage.AddressOnly);

    [Fact]
    public void ARepresentativeDocumentMapsToTheExpectedModel()
    {
        ConfigDocument document = new(
            Settings: new GlobalSettings(
                Defaults: new HostSettings { ForceHttps = true, Tls = new TlsSettings { MinimumVersion = TlsMinimumVersion.Tls13 } },
                UnknownHost: UnknownHostBehaviour.MisdirectedRequest,
                DefaultCertificateId: Id<CertificateId>(1),
                TrustedProxies: new TrustedProxySettings(TrustedProxyMode.Generic, [" 10.0.0.0/8 ", "192.168.1.1/32"], Header: "X-Real-IP")),
            Profiles: [new ProfileConfig(LanProfile, "LAN only", Settings: new HostSettings { Tls = new TlsSettings { Http3 = false }, AccessList = Use(Lan) })],
            AccessLists: [Lan, Ten],
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
                        new RouteConfig(ApiRoute, Id<UpstreamId>(2), Path: "/api", Settings: new RouteSettings { AccessList = Off }),
                        new RouteConfig(
                            Id<RouteId>(3),
                            AppUpstream,
                            Match: RouteMatchKind.Exact,
                            Path: "/admin",
                            Settings: new RouteSettings { AccessList = Use(Ten) }),
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
                    // Canonical (evaluation) order: the exact route first, then the longest prefix, then the root.
                    // The routes' lists differ, so each route carries its own.
                    Routes =
                    [
                        new RouteSpec
                        {
                            Id = Id<RouteId>(3),
                            Match = RouteMatchKind.Exact,
                            Path = "/admin",
                            Upstream = AppUpstream,
                            Policies = [Full(Ten)],
                            AccessList = Ten.Id,
                        },
                        new RouteSpec { Id = ApiRoute, Path = "/api", Upstream = Id<UpstreamId>(2), Policies = [] },
                        new RouteSpec { Id = RootRoute, Order = 10, Upstream = AppUpstream, Policies = [Full(Lan)], AccessList = Lan.Id },
                    ],
                    Policies = [],
                    RateLimit = new HostRateLimitSpec { Enabled = true },
                    AccessList = new HostAccessListSpec { Enabled = true, AccessListId = Lan.Id },
                    RuleSet = new HostRuleSetSpec { Enabled = true },
                    AuthProvider = new HostAuthProviderSpec { Enabled = true },
                },
                new HostSpec
                {
                    Id = Id<HostId>(2),
                    Names = ["plain.example.com"],
                    Https = false,
                    Tls = new HostTlsSpec { MinimumVersion = TlsMinimumVersion.Tls13 },
                    Routes = [new RouteSpec { Id = Id<RouteId>(4), Upstream = AppUpstream }],
                    RateLimit = new HostRateLimitSpec { Enabled = true },
                    AccessList = new HostAccessListSpec { Enabled = true },
                    RuleSet = new HostRuleSetSpec { Enabled = true },
                    AuthProvider = new HostAuthProviderSpec { Enabled = true },
                },
                new HostSpec
                {
                    Id = Id<HostId>(3),
                    Names = ["old.example.com"],
                    Kind = HostKind.Redirect,
                    ForceHttps = true,
                    Tls = lanTls,
                    // No route to ask a password on, and the host redirects: the list is the host chain's, its password deferred.
                    Policies = [new AccessListPolicySpec(Lan.Id, AccessListStage.Full, DeferPasswordsOnPlainHttp: true)],
                    Redirect = new RedirectSpec { Target = "https://app.example.com", StatusCode = 308 },
                    RateLimit = new HostRateLimitSpec { Enabled = true },
                    AccessList = new HostAccessListSpec { Enabled = true, AccessListId = Lan.Id },
                    RuleSet = new HostRuleSetSpec { Enabled = true },
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
                    RateLimit = new HostRateLimitSpec { Enabled = true },
                    AccessList = new HostAccessListSpec { Enabled = true },
                    RuleSet = new HostRuleSetSpec { Enabled = true },
                },
            ],
            AccessLists =
            [
                new AccessListSpec { Id = Lan.Id, Name = "lan", Realm = "lan", Allow = ["192.168.0.0/16"] },
                new AccessListSpec { Id = Ten.Id, Name = "ten", Realm = "ten", Allow = ["10.0.0.0/8"], DropDenied = true },
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
        var mapped = TestMapping.Map(document, Revision, certificates.InventoryFor(document));
        Assert.Empty(mapped.Errors);
        Assert.Equivalent(expected, mapped.Model, strict: true);
    }

    [Fact]
    public void TwoSpellingsOfOneConfigMapToTheSameModel()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var document = Random(seed);
            Assert.Equivalent(TestMapping.Map(document, Revision, CertificateInventory.Empty).Model, TestMapping.Map(Respelled(document, seed), Revision, CertificateInventory.Empty).Model, strict: true);
        }
    }

    [Fact]
    public void WithoutRouteOverridesTheListGuardsTheWholeHost()
    {
        // Also when a route "overrides" it with the same list.
        var host = MapOnlyHost(new HostSettings { AccessList = Use(Lan) }, new RouteSettings(), new RouteSettings { AccessList = Use(Lan) });

        Assert.Equal(Full(Lan), Assert.Single(host.Policies));
        Assert.All(host.Routes, r => Assert.Empty(r.Policies));
    }

    [Fact]
    public void RoutesThatAllNameTheSameListGuardTheWholeHost()
    {
        var host = MapOnlyHost(null, new RouteSettings { AccessList = Use(Lan) }, new RouteSettings { AccessList = Use(Lan) });

        Assert.Equal(Full(Lan), Assert.Single(host.Policies));
        Assert.All(host.Routes, r => Assert.Empty(r.Policies));
    }

    [Fact]
    public void ARouteCanNameAnotherList()
    {
        var host = MapOnlyHost(new HostSettings { AccessList = Use(Lan) }, new RouteSettings { AccessList = Use(Ten) }, new RouteSettings { AccessList = Use(Seven) });

        // Each route carries its own list, so an override replaces the host's instead of stacking under it; the host chain
        // carries their address rules together, so a client no route admits can't tell the paths apart.
        Assert.Equal([Ten.Id, Seven.Id], Assert.IsType<AccessListUnionPolicySpec>(Assert.Single(host.Policies)).Lists);
        Assert.Equal(Full(Ten), Assert.Single(host.Routes[0].Policies));
        Assert.Equal(Full(Seven), Assert.Single(host.Routes[1].Policies));
    }

    [Fact]
    public void TheUnionNamesEachListOnceInRouteOrder()
    {
        var host = MapOnlyHost(new HostSettings { AccessList = Use(Lan) }, new RouteSettings(), new RouteSettings { AccessList = Use(Seven) });

        Assert.Equal([Lan.Id, Seven.Id], Assert.IsType<AccessListUnionPolicySpec>(Assert.Single(host.Policies)).Lists);
        Assert.Equal(Full(Lan), Assert.Single(host.Routes[0].Policies));
        Assert.Equal(Full(Seven), Assert.Single(host.Routes[1].Policies));
    }

    [Fact]
    public void ARouteCanTurnTheHostsListOff()
    {
        var host = MapOnlyHost(new HostSettings { AccessList = Use(Lan) }, new RouteSettings(), new RouteSettings { AccessList = Off });

        // Anyone may reach the lifted route, so the host chain hides nothing.
        Assert.Empty(host.Policies);
        Assert.Equal(Full(Lan), Assert.Single(host.Routes[0].Policies));
        Assert.Empty(host.Routes[1].Policies);
    }

    [Fact]
    public void ARouteCanGuardAnUnguardedHost()
    {
        var host = MapOnlyHost(null, new RouteSettings(), new RouteSettings { AccessList = Use(Lan) });

        Assert.Empty(host.Policies);
        Assert.Empty(host.Routes[0].Policies);
        Assert.Equal(Full(Lan), Assert.Single(host.Routes[1].Policies));
    }

    [Fact]
    public void ARouteKeepsTheReferenceWhileItIsOff()
    {
        var host = MapOnlyHost(null, new RouteSettings(), new RouteSettings { AccessList = new AccessListSettings { Enabled = false, AccessListId = Lan.Id } });

        Assert.All(host.Routes, r => Assert.Empty(r.Policies));
        Assert.Empty(host.Policies);
    }

    [Fact]
    public void PasswordsAreAskedOnTheRouteAndTheHostChainOnlyRefusesByAddress()
    {
        var host = MapOnlyHost(new HostSettings { AccessList = Use(LanAndPassword) }, new RouteSettings(), new RouteSettings());

        Assert.Equal(AddressOnly(LanAndPassword), Assert.Single(host.Policies));
        Assert.All(host.Routes, r => Assert.Equal(Full(LanAndPassword), Assert.Single(r.Policies)));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(4)]
    public void AListThatNoAddressCanRefuseLeavesTheHostChainEmpty(int list)
    {
        // Satisfy "any" with an allow list, or users alone: only the password can turn a request away, and it is asked on the route.
        var config = AllLists.Single(l => l.Id == Id<AccessListId>(list));
        var host = MapOnlyHost(new HostSettings { AccessList = Use(config) }, new RouteSettings(), new RouteSettings());

        Assert.Empty(host.Policies);
        Assert.All(host.Routes, r => Assert.Equal(Full(config), Assert.Single(r.Policies)));
    }

    [Fact]
    public void ADenyEntryRefusesByAddressWhateverTheSatisfyMode()
    {
        AccessListConfig vetoed = LanOrPassword with { Id = Id<AccessListId>(7), Ip = new AccessListIp(Allow: ["192.168.0.0/16"], Deny: ["192.168.9.0/24"]) };
        var host = MapOnlyHost(new HostSettings { AccessList = Use(vetoed) }, new RouteSettings(), new RouteSettings(), vetoed);

        Assert.Equal(AddressOnly(vetoed), Assert.Single(host.Policies));
    }

    [Fact]
    public void AHostWithoutRoutesCarriesTheWholeListAndDefersPasswordsOnlyWhenItRedirects()
    {
        foreach (var (forceHttps, deferred) in new[] { (true, true), (false, false) })
        {
            var document = Document(null, null, new HostSettings { ForceHttps = forceHttps, AccessList = Use(Passwords) });
            var host = document.Hosts[0] with { Kind = HostKind.Respond, Routes = [], Respond = new RespondConfig(503, "soon"), Certificate = new CertificateBinding { Mode = CertificateBindingMode.None } };

            var mapped = Assert.Single(TestMapping.Map(document with { Hosts = [host], AccessLists = AllLists }, Revision, CertificateInventory.Empty).Model.Hosts);

            Assert.Equal(new AccessListPolicySpec(Passwords.Id, AccessListStage.Full, deferred), Assert.Single(mapped.Policies));
        }
    }

    [Fact]
    public void AHostThatAsksForPasswordsOverPlainHttpIsWarnedAbout()
    {
        var mapped = (bool forceHttps) => TestMapping.Map(
            Document(null, null, new HostSettings { ForceHttps = forceHttps, AccessList = Use(Passwords) }) with { AccessLists = AllLists }, Revision, CertificateInventory.Empty);

        Assert.Contains(mapped(false).Warnings, w => w.Code == ConfigWarningCodes.AccessListInsecureCredentials);
        Assert.DoesNotContain(mapped(true).Warnings, w => w.Code == ConfigWarningCodes.AccessListInsecureCredentials);
    }

    [Fact]
    public void AnAddressOnlyListNeverWarnsAboutPasswords()
    {
        var document = Document(null, null, new HostSettings { AccessList = Use(Lan) }) with { AccessLists = AllLists };

        Assert.DoesNotContain(TestMapping.Map(document, Revision, CertificateInventory.Empty).Warnings, w => w.Code == ConfigWarningCodes.AccessListInsecureCredentials);
    }

    [Fact]
    public void AnAccessListMapsItsAddressesUsersRealmAndSatisfyMode()
    {
        AccessListConfig config = new(
            Id<AccessListId>(9),
            "Staff area",
            Ip: new AccessListIp([" 10.0.0.0/8", "10.0.0.0/8", "192.168.1.7/32"], ["::ffff:10.9.0.0/112"], DropDenied: true),
            BasicAuth: new AccessListBasicAuth(" Staff only ", [new AccessListUserConfig("bob", Id<PasswordCredentialId>(2)), new AccessListUserConfig("alice", Id<PasswordCredentialId>(1))]),
            Satisfy: AccessListSatisfy.Any,
            PassCredentialsUpstream: true);

        var spec = Assert.Single(TestMapping.Map(Document(null, null, null) with { AccessLists = [config] }, Revision, CertificateInventory.Empty).Model.AccessLists);

        Assert.Equal(["10.0.0.0/8", "192.168.1.7"], spec.Allow);
        Assert.Equal(["10.9.0.0/16"], spec.Deny);
        Assert.True(spec.DropDenied);
        Assert.True(spec.PassCredentialsUpstream);
        Assert.Equal(AccessListSatisfy.Any, spec.Satisfy);
        Assert.Equal("Staff only", spec.Realm);
        Assert.Equal(["alice", "bob"], spec.Users.Select(u => u.Username));
        Assert.Equal([Id<PasswordCredentialId>(1), Id<PasswordCredentialId>(2)], spec.Users.Select(u => u.Credential.Id));
    }

    [Fact]
    public async Task ACredentialInTheOverlayCompilesAgainstThePreparedHandleAndTouchesNothingElse()
    {
        var passwords = new TestPasswords();
        var document = Document(null, null, null) with { AccessLists = [Passwords] };
        var credential = Passwords.BasicAuth!.Users[0].CredentialId!.Value;
        var prepared = await passwords.PrepareAsync(credential, "draft".AsMemory(), TestContext.Current.CancellationToken);

        var mapped = ProxyConfigMapping.Map(document, Revision, CertificateInventory.Empty, passwords, new PendingPasswords(
            new HashSet<PasswordCredentialId>(), new Dictionary<PasswordCredentialId, IPasswordCredential> { [credential] = prepared }));

        Assert.Same(prepared, Assert.Single(Assert.Single(mapped.Model.AccessLists).Users).Credential);
        Assert.DoesNotContain(mapped.Warnings, w => w.Code == ConfigWarningCodes.AccessListUserNoPassword);
        Assert.False(passwords.Open(credential).IsSet);
        Assert.Empty(await passwords.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AUserWhoseCredentialHasNoPasswordIsAWarningNeverAnErrorUnlessItsPasswordIsPending()
    {
        var passwords = new TestPasswords();
        var document = Document(null, null, null) with { AccessLists = [Passwords] };
        var credential = Passwords.BasicAuth!.Users[0].CredentialId!.Value;

        var missing = ProxyConfigMapping.Map(document, Revision, CertificateInventory.Empty, passwords, PendingPasswords.None);
        var pending = ProxyConfigMapping.Map(document, Revision, CertificateInventory.Empty, passwords, new PendingPasswords(new HashSet<PasswordCredentialId> { credential }, new Dictionary<PasswordCredentialId, IPasswordCredential>()));
        await passwords.SetAsync(credential, "secret".AsMemory(), TestContext.Current.CancellationToken);
        var held = ProxyConfigMapping.Map(document, Revision, CertificateInventory.Empty, passwords, PendingPasswords.None);

        Assert.Empty(missing.Errors);
        var warning = Assert.Single(missing.Warnings, w => w.Code == ConfigWarningCodes.AccessListUserNoPassword);
        Assert.Contains("'alice'", warning.Message, StringComparison.Ordinal);

        // The warning points at the user's own entry, so nothing needs to read its text.
        Assert.Equal("/basicAuth/users/0", warning.Member);
        Assert.DoesNotContain(pending.Warnings, w => w.Code == ConfigWarningCodes.AccessListUserNoPassword);
        Assert.DoesNotContain(held.Warnings, w => w.Code == ConfigWarningCodes.AccessListUserNoPassword);
        Assert.True(Assert.Single(Assert.Single(held.Model.AccessLists).Users).Credential.IsSet);
    }

    [Fact]
    public void TheRealmIsTheListsNameUnlessItSaysOtherwise()
    {
        var spec = Assert.Single(TestMapping.Map(Document(null, null, null) with { AccessLists = [List(1, "  Family  ", users: true)] }, Revision, CertificateInventory.Empty).Model.AccessLists);

        Assert.Equal("Family", spec.Realm);
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

        var host = Assert.Single(TestMapping.Map(admin, Revision, CertificateInventory.Empty).Model.Hosts);

        Assert.Equal(HostKind.Admin, host.Kind);
        Assert.Equal(new HostHstsSpec { Enabled = true, MaxAgeSeconds = 86_400, IncludeSubDomains = false, Preload = false }, host.Hsts);
    }

    [Fact]
    public void AHostCanTurnAnInheritedListOff()
    {
        var document = Document(
            defaults: new HostSettings { AccessList = Use(Lan) },
            profile: new HostSettings(),
            host: new HostSettings { AccessList = Off }) with
        { AccessLists = AllLists };

        var host = Assert.Single(TestMapping.Map(document, Revision, CertificateInventory.Empty).Model.Hosts);

        Assert.Empty(host.Policies);
        Assert.Equal(new HostAccessListSpec { Enabled = false, AccessListId = Lan.Id }, host.AccessList);
    }

    [Fact]
    public void RoutesInheritOverrideOrTurnOffTheHostsRateLimit()
    {
        var document = new ConfigDocument(
            Upstreams: [new UpstreamConfig(AppUpstream, "app", Destinations: [new DestinationConfig(AppDestination, "http://app")])],
            RateLimitPolicies:
            [
                new RateLimitPolicyConfig(ApiPolicy, "api"),
                new RateLimitPolicyConfig(OtherPolicy, "other"),
            ],
            Hosts:
            [
                new HostConfig(
                    AppHost,
                    ["app.example.com"],
                    Settings: new HostSettings { RateLimit = new RateLimitSettings { PolicyId = ApiPolicy } },
                    Routes:
                    [
                        new RouteConfig(RootRoute, AppUpstream),
                        new RouteConfig(ApiRoute, AppUpstream, Path: "/off", Settings: new RouteSettings { RateLimit = new RateLimitSettings { Enabled = false } }),
                        new RouteConfig(Id<RouteId>(3), AppUpstream, Path: "/other", Settings: new RouteSettings { RateLimit = new RateLimitSettings { PolicyId = OtherPolicy } }),
                    ]),
            ]);

        Assert.Empty(ConfigValidator.Validate(document));
        var host = Assert.Single(TestMapping.Map(document, Revision, CertificateInventory.Empty).Model.Hosts);
        Dictionary<string, RouteSpec> routes = host.Routes.ToDictionary(r => r.Path);

        Assert.Equal(ApiPolicy, routes["/"].RateLimit);
        Assert.False(routes["/"].RateLimitRouteOverride);
        Assert.Null(routes["/off"].RateLimit);
        Assert.True(routes["/off"].RateLimitRouteOverride);
        Assert.Equal(OtherPolicy, routes["/other"].RateLimit);
        Assert.True(routes["/other"].RateLimitRouteOverride);
    }

    [Fact]
    public void AMissingProfileAddsNoLayer()
    {
        var document = Document(new HostSettings { ForceHttps = true }, new HostSettings { ForceHttps = false }, null);
        document = document with { Hosts = [document.Hosts[0] with { ProfileId = Id<ProfileId>(99) }] };

        Assert.True(Assert.Single(TestMapping.Map(document, Revision, CertificateInventory.Empty).Model.Hosts).ForceHttps);
    }

    [Fact]
    public void CertificateBindings()
    {
        using AnyCertificate certificates = new();
        HostSpec Bound(CertificateBinding binding)
        {
            var document = Document(null, null, null);
            var bound = document with { Hosts = [document.Hosts[0] with { Certificate = binding }] };
            return Assert.Single(TestMapping.Map(bound, Revision, certificates.InventoryFor(bound)).Model.Hosts);
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

        var mapped = TestMapping.Map(changed, Revision, CertificateInventory.Empty);

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

            Assert.Equal(policy.ToString(), Assert.Single(TestMapping.Map(changed, Revision, CertificateInventory.Empty).Model.Upstreams).LoadBalancing);
        }

        Assert.Null(Assert.Single(TestMapping.Map(Valid(), Revision, CertificateInventory.Empty).Model.Upstreams).LoadBalancing);
    }

    [Fact]
    public void AnAdminHostsExplicitForceHttpsFalseWarnsThatItIsIgnored()
    {
        ConfigDocument document = new(
            Hosts: [new HostConfig(Id<HostId>(50), ["admin.example.com"], Kind: HostKind.Admin, Settings: new HostSettings { ForceHttps = false })]);

        var mapped = TestMapping.Map(document, Revision, CertificateInventory.Empty);

        var warning = Assert.Single(mapped.Warnings, w => w.Code == ConfigWarningCodes.AdminForceHttpsIgnored);
        Assert.Equal("/settings/forceHttps", warning.Member);
        Assert.Equal(CompileErrorSource.Host(Id<HostId>(50)), warning.Source);
        Assert.Contains("always served over HTTPS", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdminHostNeverReplacesItsUpstreamsErrorsWhateverTheDefaultsSay()
    {
        ConfigDocument document = new(
            Hosts: [new HostConfig(Id<HostId>(50), ["admin.example.com"], Kind: HostKind.Admin)],
            Settings: new GlobalSettings(Defaults: new HostSettings { ErrorPages = new ErrorPagesSettings { ReplaceUpstream = UpstreamErrorReplacement.Any } }));

        var mapped = TestMapping.Map(document, Revision, CertificateInventory.Empty);

        Assert.Equal(UpstreamErrorReplacement.Off, Assert.Single(mapped.Model.Hosts).ErrorPages.ReplaceUpstream);
        var warning = Assert.Single(mapped.Warnings, w => w.Code == ConfigWarningCodes.ErrorPagesReplaceAdminIgnored);
        Assert.Equal("/settings/errorPages/replaceUpstream", warning.Member);
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

        var mapped = TestMapping.Map(document, Revision, CertificateInventory.Empty);

        var pinned = Assert.Single(mapped.Model.Upstreams, u => u.Id == Id<UpstreamId>(1));
        Assert.Equal(UpstreamTlsTrust.Pinned, pinned.TlsTrust);
        // Colons dropped, lowercased, and the two spellings of one fingerprint kept once.
        Assert.Equal([string.Concat(Enumerable.Repeat("ab", 32)), new string('b', 64)], pinned.PinnedSha256.Order(StringComparer.Ordinal));
        var warning = Assert.Single(mapped.Warnings, w => w.Code == ConfigWarningCodes.UpstreamTlsUnverified);
        Assert.Equal(CompileErrorSource.Upstream(Id<UpstreamId>(2)), warning.Source);
        Assert.Equal("/tls/trust", warning.Member);
    }

    [Fact]
    public void ProofOfWorkResolvesPerHostAndARouteOverridesOnlyTheSwitch()
    {
        HostSettings on = new() { ProofOfWork = new ProofOfWorkSettings { Enabled = true, Difficulty = 20, TokenLifetimeHours = 24 } };
        var document = Document(on, null, null);
        var mapped = TestMapping.Map(document, Revision, CertificateInventory.Empty);
        var host = Assert.Single(mapped.Model.Hosts);
        Assert.Equal(new HostProofOfWorkSpec { Enabled = true, Difficulty = 20, TokenLifetimeHours = 24 }, host.ProofOfWork);
        Assert.All(host.Routes, route => Assert.Equal(host.ProofOfWork, route.ProofOfWork));
        Assert.All(host.Routes, route => Assert.False(route.ProofOfWorkRouteOverride));

        // The route's own switch wins either way, and the host's values stay.
        var exempt = MapOnlyHost(on,
            new RouteSettings { ProofOfWork = new ProofOfWorkSettings { Enabled = false } },
            new RouteSettings());
        Assert.Equal(new HostProofOfWorkSpec { Enabled = false, Difficulty = 20, TokenLifetimeHours = 24 }, exempt.Routes[0].ProofOfWork);
        Assert.True(exempt.Routes[0].ProofOfWorkRouteOverride);
        Assert.Equal(exempt.ProofOfWork, exempt.Routes[1].ProofOfWork);
        Assert.False(exempt.Routes[1].ProofOfWorkRouteOverride);

        var protectedRoute = MapOnlyHost(null,
            new RouteSettings { ProofOfWork = new ProofOfWorkSettings { Enabled = true } },
            new RouteSettings());
        Assert.False(protectedRoute.ProofOfWork.Enabled);
        Assert.True(protectedRoute.Routes[0].ProofOfWork.Enabled);
        Assert.True(protectedRoute.Routes[0].ProofOfWorkRouteOverride);
        Assert.False(protectedRoute.Routes[1].ProofOfWork.Enabled);

        // The cookie cannot be Secure over plain HTTP, so a host that doesn't force the redirect warns.
        var warning = Assert.Single(mapped.Warnings, w => w.Code == ConfigWarningCodes.ProofOfWorkInsecureCookie);
        Assert.Equal(CompileErrorSource.Host(AppHost), warning.Source);
        Assert.DoesNotContain(
            TestMapping.Map(Document(on with { ForceHttps = true }, null, null), Revision, CertificateInventory.Empty).Warnings,
            w => w.Code == ConfigWarningCodes.ProofOfWorkInsecureCookie);
        Assert.DoesNotContain(
            TestMapping.Map(Document(null, null, null), Revision, CertificateInventory.Empty).Warnings,
            w => w.Code == ConfigWarningCodes.ProofOfWorkInsecureCookie);
    }

    [Fact]
    public void RequestLimitsResolvePerHostAndARouteOverridesOnlyTheBodyLimit()
    {
        HostSettings limits = new() { RequestLimits = new RequestLimitsSettings { MaxBodySize = 10 * 1024 * 1024, MaxHeaderCount = 30, MaxHeaderBytes = 16384, MaxUrlLength = 2048 } };
        var host = Assert.Single(TestMapping.Map(Document(limits, null, null), Revision, CertificateInventory.Empty).Model.Hosts);
        Assert.Equal(new HostRequestLimitsSpec { MaxBodySize = 10 * 1024 * 1024, MaxHeaderCount = 30, MaxHeaderBytes = 16384, MaxUrlLength = 2048 }, host.RequestLimits);
        Assert.All(host.Routes, route => Assert.False(route.MaxBodySizeRouteOverride));
        Assert.All(host.Routes, route => Assert.Equal(10 * 1024 * 1024, route.MaxBodySize));

        // An upload route takes more than the host, or any body (0 is no limit); a route without its own inherits the host's.
        var larger = MapOnlyHost(limits,
            new RouteSettings { RequestLimits = new RequestLimitsSettings { MaxBodySize = 1024L * 1024 * 1024 } },
            new RouteSettings());
        Assert.Equal(1024L * 1024 * 1024, larger.Routes[0].MaxBodySize);
        Assert.True(larger.Routes[0].MaxBodySizeRouteOverride);
        Assert.Equal(10 * 1024 * 1024, larger.Routes[1].MaxBodySize);
        Assert.False(larger.Routes[1].MaxBodySizeRouteOverride);
        var unlimited = MapOnlyHost(limits,
            new RouteSettings { RequestLimits = new RequestLimitsSettings { MaxBodySize = RequestLimitBounds.UnlimitedBodySize } },
            new RouteSettings());
        Assert.Equal(RequestLimitBounds.UnlimitedBodySize, unlimited.Routes[0].MaxBodySize);
        Assert.True(unlimited.Routes[0].MaxBodySizeRouteOverride);

        // Nothing set anywhere: the built-in limits.
        var builtIn = Assert.Single(TestMapping.Map(Document(null, null, null), Revision, CertificateInventory.Empty).Model.Hosts);
        Assert.Equal(HostRequestLimitsSpec.Default, builtIn.RequestLimits);
    }

    [Fact]
    public void CloudflareSettingsReachTheHostAndWarnWhereTheyCanEnforceNothing()
    {
        HostSettings locked = new() { Cloudflare = new CloudflareSettings { OriginLockdown = OriginLockdownMode.Forbid, AuthenticatedOriginPulls = true } };
        static GlobalSettings Mode(TrustedProxyMode mode, HostSettings? defaults = null) =>
            new(Defaults: defaults, TrustedProxies: new TrustedProxySettings(mode, mode == TrustedProxyMode.CloudflareTunnel ? ["127.0.0.1"] : []));

        // Inherited from the defaults, carried by the host.
        var document = Document(null, null, null);
        var mapped = TestMapping.Map(document with { Settings = Mode(TrustedProxyMode.CloudflareProxy, locked) }, Revision, CertificateInventory.Empty);
        var host = Assert.Single(mapped.Model.Hosts);
        Assert.Equal(new HostCloudflareSpec { OriginLockdown = OriginLockdownMode.Forbid, AuthenticatedOriginPulls = true }, host.Cloudflare);
        Assert.DoesNotContain(mapped.Warnings, w => w.Code.StartsWith("cloudflare.", StringComparison.Ordinal));

        // The same settings under a mode that never trusts Cloudflare enforce nothing, and the host says so.
        foreach (var mode in new[] { TrustedProxyMode.Off, TrustedProxyMode.Generic })
        {
            var inert = TestMapping.Map(document with { Settings = Mode(mode, locked) }, Revision, CertificateInventory.Empty);
            var warning = Assert.Single(inert.Warnings, w => w.Code == ConfigWarningCodes.CloudflareNeedsMode);
            Assert.Equal(CompileErrorSource.Host(AppHost), warning.Source);
            Assert.Equal("/settings/cloudflare", warning.Member);
        }

        // Nothing set, nothing said.
        Assert.DoesNotContain(
            TestMapping.Map(document with { Settings = Mode(TrustedProxyMode.Off) }, Revision, CertificateInventory.Empty).Warnings,
            w => w.Code.StartsWith("cloudflare.", StringComparison.Ordinal));

        // Authenticated Origin Pulls on a host with no HTTPS can only ever refuse, and so can it under a tunnel, where
        // cloudflared presents no client certificate.
        var plain = document with
        {
            Settings = Mode(TrustedProxyMode.CloudflareProxy, locked),
            Hosts = [document.Hosts[0] with { Certificate = new CertificateBinding { Mode = CertificateBindingMode.None } }],
        };
        var plainWarnings = TestMapping.Map(plain, Revision, CertificateInventory.Empty).Warnings;
        Assert.Contains(plainWarnings, w => w.Code == ConfigWarningCodes.CloudflareAopNeedsHttps);
        Assert.DoesNotContain(plainWarnings, w => w.Code == ConfigWarningCodes.CloudflareAopTunnel);
        var tunnelWarnings = TestMapping.Map(document with { Settings = Mode(TrustedProxyMode.CloudflareTunnel, locked) }, Revision, CertificateInventory.Empty).Warnings;
        Assert.Contains(tunnelWarnings, w => w.Code == ConfigWarningCodes.CloudflareAopTunnel);
        Assert.DoesNotContain(tunnelWarnings, w => w.Code == ConfigWarningCodes.CloudflareAopNeedsHttps);
    }

    // One proxy host with two equally specific routes ("/a" and "/b") under a profile-less, default-less document,
    // so their listed order is the evaluation order and each index is the route the caller passed.
    private static readonly AccessListConfig Germany = Lan with
    {
        Id = Id<AccessListId>(20),
        Name = "germany",
        Ip = new AccessListIp(Countries: new AccessListCountries(["DE", "NL"], ["RU", "T1"])),
    };

    [Fact]
    public void AnAccessListMapsItsCountryRules()
    {
        var document = Document(null, null, new HostSettings { AccessList = Use(Germany) }) with { AccessLists = [Germany] };

        var spec = Assert.Single(TestMapping.Map(document, Revision, CertificateInventory.Empty).Model.AccessLists);

        Assert.Equal(["DE", "NL"], spec.AllowCountries);
        Assert.Equal(["RU", "T1"], spec.DenyCountries);
        Assert.Empty(spec.Allow);
    }

    [Theory]
    [InlineData(TrustedProxyMode.Off, true)]
    [InlineData(TrustedProxyMode.Generic, true)]
    [InlineData(TrustedProxyMode.CloudflareProxy, false)]
    [InlineData(TrustedProxyMode.CloudflareTunnel, false)]
    public void CountryRulesWarnWhenNothingCanSupplyACountry(TrustedProxyMode mode, bool warns)
    {
        var document = Document(null, null, new HostSettings { AccessList = Use(Germany) }) with
        {
            AccessLists = [Germany],
            Settings = new GlobalSettings(TrustedProxies: new TrustedProxySettings(mode, mode == TrustedProxyMode.CloudflareProxy ? [] : ["10.0.0.0/8"])),
        };

        var warnings = TestMapping.Map(document, Revision, CertificateInventory.Empty).Warnings.Where(w => w.Code == ConfigWarningCodes.AccessListCountriesNoSource).ToList();

        Assert.Equal(warns ? 1 : 0, warnings.Count);
        if (warns)
        {
            Assert.Equal("/settings/accessList", warnings[0].Member);
        }
    }

    [Fact]
    public void ARouteThatNamesACountryListWarnsToo()
    {
        var document = Document(null, null, null) with { AccessLists = [Germany] };
        var host = document.Hosts[0] with { Routes = [new RouteConfig(RootRoute, AppUpstream, Settings: new RouteSettings { AccessList = Use(Germany) })] };

        var mapped = TestMapping.Map(document with { Hosts = [host] }, Revision, CertificateInventory.Empty);

        Assert.Contains(mapped.Warnings, w => w.Code == ConfigWarningCodes.AccessListCountriesNoSource);
    }

    [Fact]
    public void NoWarningWithoutCountryRulesOrForADisabledHost()
    {
        var plain = Document(null, null, new HostSettings { AccessList = Use(Lan) }) with { AccessLists = [Lan] };
        var disabled = Document(null, null, new HostSettings { AccessList = Use(Germany) }) with { AccessLists = [Germany] };
        disabled = disabled with { Hosts = [disabled.Hosts[0] with { Enabled = false }] };
        var off = Document(null, null, new HostSettings { AccessList = Off }) with { AccessLists = [Germany] };

        foreach (var document in new[] { plain, disabled, off })
        {
            Assert.DoesNotContain(TestMapping.Map(document, Revision, CertificateInventory.Empty).Warnings, w => w.Code == ConfigWarningCodes.AccessListCountriesNoSource);
        }
    }

    [Fact]
    public void AnAllowedCountryRefusesTheRestByAddressWhenBothMustPass()
    {
        AccessListConfig list = Passwords with { Id = Id<AccessListId>(23), Ip = new AccessListIp(Countries: new AccessListCountries(["DE"], [])) };
        var host = MapOnlyHost(new HostSettings { AccessList = Use(list) }, new RouteSettings(), new RouteSettings(), list);

        Assert.Equal(AddressOnly(list), Assert.Single(host.Policies));
    }

    [Fact]
    public void AllowedCountriesAloneCantRefuseWhenAPasswordCanLetEveryoneElseIn()
    {
        AccessListConfig list = LanOrPassword with { Id = Id<AccessListId>(21), Ip = new AccessListIp(Countries: new AccessListCountries(["DE"], [])) };
        var host = MapOnlyHost(new HostSettings { AccessList = Use(list) }, new RouteSettings(), new RouteSettings(), list);

        Assert.Empty(host.Policies);
    }

    [Fact]
    public void ADeniedCountryRefusesByAddressWhateverTheSatisfyMode()
    {
        AccessListConfig list = LanOrPassword with { Id = Id<AccessListId>(22), Ip = new AccessListIp(Countries: new AccessListCountries([], ["RU"])) };
        var host = MapOnlyHost(new HostSettings { AccessList = Use(list) }, new RouteSettings(), new RouteSettings(), list);

        Assert.Equal(AddressOnly(list), Assert.Single(host.Policies));
    }

    private static HostSpec MapOnlyHost(HostSettings? host, RouteSettings first, RouteSettings second, params AccessListConfig[] more)
    {
        var document = Document(null, null, host) with { AccessLists = [.. AllLists, .. more] };
        var config = document.Hosts[0] with
        {
            Routes = [new RouteConfig(RootRoute, AppUpstream, Path: "/a", Settings: first), new RouteConfig(ApiRoute, AppUpstream, Path: "/b", Settings: second)],
        };
        return Assert.Single(TestMapping.Map(document with { Hosts = [config] }, Revision, CertificateInventory.Empty).Model.Hosts);
    }

    private static ConfigDocument Document(HostSettings? defaults, HostSettings? profile, HostSettings? host) => new(
        Settings: new GlobalSettings(Defaults: defaults),
        Profiles: profile is null ? [] : [new ProfileConfig(LanProfile, "profile", Settings: profile)],
        Upstreams: [new UpstreamConfig(AppUpstream, "app", Destinations: [new DestinationConfig(AppDestination, "http://app")])],
        Hosts: [new HostConfig(AppHost, ["app.example.com"], ProfileId: profile is null ? null : LanProfile, Settings: host, Routes: [new RouteConfig(RootRoute, AppUpstream)])]);
}
