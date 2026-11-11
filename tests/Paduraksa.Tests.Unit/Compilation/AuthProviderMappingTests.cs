using Paduraksa.Application.Certificates;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.ForwardAuth;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Compilation;

/// <summary>What a provider resolves to (the preset's defaults under the explicit values), how a host and its routes inherit, override and
/// turn it off, which hosts ignore it, and that the compiler accepts what the mapping hands it.</summary>
public sealed class AuthProviderMappingTests
{
    private static readonly ConfigRevisionId Revision = Id<ConfigRevisionId>(7);
    private static readonly AuthProviderId Sso = Id<AuthProviderId>(1);
    private static readonly AuthProviderId Other = Id<AuthProviderId>(2);

    private static AuthProviderSettings Use(AuthProviderId id) => new() { AuthProviderId = id };

    [Fact]
    public void ANamedProviderGuardsEveryRouteAndTheHostRecordsIt()
    {
        var host = MapHost(new HostSettings { AuthProvider = Use(Sso) }, new RouteSettings(), new RouteSettings());

        Assert.Equal(new HostAuthProviderSpec { Enabled = true, AuthProviderId = Sso }, host.AuthProvider);
        Assert.All(host.Routes, route =>
        {
            Assert.Equal(Sso, route.AuthProvider);
            Assert.False(route.AuthProviderRouteOverride);
        });
        // The provider is the route chain's business (the compiler puts it after the rate limit): the document's chains carry nothing for it.
        Assert.Empty(host.Policies);
        Assert.All(host.Routes, route => Assert.Empty(route.Policies));
    }

    [Fact]
    public void ARouteNamesAnotherProviderOrTurnsAuthenticationOffAndAnInheritingOneIsNotAnOverride()
    {
        var host = MapHost(
            new HostSettings { AuthProvider = Use(Sso) },
            new RouteSettings { AuthProvider = Use(Other) },
            new RouteSettings { AuthProvider = new AuthProviderSettings { Enabled = false } },
            new RouteSettings());

        Assert.Equal((Other, true), (host.Routes[0].AuthProvider, host.Routes[0].AuthProviderRouteOverride));
        Assert.Equal((null, true), (host.Routes[1].AuthProvider, host.Routes[1].AuthProviderRouteOverride));
        Assert.Equal((Sso, false), (host.Routes[2].AuthProvider, host.Routes[2].AuthProviderRouteOverride));
    }

    [Fact]
    public void ARouteCanGuardAnOpenHostAndAReferenceKeptWhileOffGuardsNothing()
    {
        var host = MapHost(null, new RouteSettings(), new RouteSettings { AuthProvider = Use(Sso) }, new RouteSettings { AuthProvider = new AuthProviderSettings { Enabled = false, AuthProviderId = Sso } });

        Assert.Null(host.Routes[0].AuthProvider);
        Assert.Equal(Sso, host.Routes[1].AuthProvider);
        Assert.Null(host.Routes[2].AuthProvider);
        Assert.Null(host.AuthProvider.Effective);
    }

    [Fact]
    public void TheDefaultsAndAProfileLayerThroughToTheHost()
    {
        var document = Document(new HostSettings { AuthProvider = Use(Sso) }, new HostSettings { AuthProvider = new AuthProviderSettings { Enabled = false } }, null);

        var host = Assert.Single(Map(document).Model.Hosts);

        // The profile turns the switch off and keeps the defaults' provider, which a route can still name for itself.
        Assert.False(host.AuthProvider.Enabled);
        Assert.Equal(Sso, host.AuthProvider.AuthProviderId);
        Assert.Null(host.Routes[0].AuthProvider);
    }

    [Theory]
    [InlineData(ForwardAuthPreset.Authelia, "http://authelia:9091", "http://authelia:9091/api/authz/forward-auth", ForwardAuthCacheScope.Request, new[] { "Remote-Email", "Remote-Groups", "Remote-Name", "Remote-User" }, null)]
    [InlineData(ForwardAuthPreset.Authentik, "http://authentik:9000/", "http://authentik:9000/outpost.goauthentik.io/auth/traefik", ForwardAuthCacheScope.Host, new[] { "X-authentik-*" }, null)]
    [InlineData(ForwardAuthPreset.OAuth2Proxy, "http://oauth2-proxy:4180", "http://oauth2-proxy:4180/oauth2/auth", ForwardAuthCacheScope.Host, new[] { "X-Auth-Request-Email", "X-Auth-Request-Groups", "X-Auth-Request-Preferred-Username", "X-Auth-Request-User" }, "/oauth2/start")]
    public void APresetFillsInTheEndpointPathTheHeadersTheScopeAndTheSignIn(ForwardAuthPreset preset, string url, string expectedUrl, ForwardAuthCacheScope scope, string[] responseHeaders, string? signIn)
    {
        var spec = MapProvider(new ForwardAuthConfig(url, preset));

        Assert.Equal(expectedUrl, spec.Url);
        Assert.Equal(scope, spec.CacheScope);
        Assert.Equal(responseHeaders, spec.ResponseHeaders);
        Assert.Equal(signIn, spec.SignInUrl);
        Assert.Equal(signIn is null ? null : "rd", spec.SignInReturnParameter);
        Assert.Equal((TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)), (spec.Timeout, spec.CacheTtl));
    }

    [Fact]
    public void ExplicitValuesWinOverThePresetAndAUrlWithAPathIsUsedAsItIs()
    {
        var spec = MapProvider(new ForwardAuthConfig(
            "http://authelia:9091/custom/path",
            ForwardAuthPreset.Authelia,
            TimeoutSeconds: 2,
            CacheSeconds: 0,
            CacheScope: ForwardAuthCacheScope.Host,
            RequestHeaders: ["X-Tenant"],
            ResponseHeaders: ["Remote-User"],
            SignIn: new ForwardAuthSignInConfig("https://login.example.com/in", "next")));

        Assert.Equal("http://authelia:9091/custom/path", spec.Url);
        Assert.Equal((TimeSpan.FromSeconds(2), TimeSpan.Zero, ForwardAuthCacheScope.Host), (spec.Timeout, spec.CacheTtl, spec.CacheScope));
        Assert.Equal(["X-Tenant"], spec.RequestHeaders);
        Assert.Equal(["Remote-User"], spec.ResponseHeaders);
        Assert.Equal(("https://login.example.com/in", "next"), (spec.SignInUrl, spec.SignInReturnParameter));
    }

    [Fact]
    public void ACustomProviderTakesItsUrlWholeAndAnEmptyResponseListMeansNone()
    {
        var spec = MapProvider(new ForwardAuthConfig("http://auth.internal/", ResponseHeaders: []));

        Assert.Equal("http://auth.internal/", spec.Url);
        Assert.Empty(spec.ResponseHeaders);
        Assert.Null(spec.SignInUrl);
        Assert.Equal(ForwardAuthCacheScope.Request, spec.CacheScope);
    }

    [Fact]
    public void ARepresentativeDocumentCompilesAndTheRouteGetsItsPolicy()
    {
        var document = Document(new HostSettings { AuthProvider = Use(Sso) }, null, null) with
        {
            AuthProviders = [new AuthProviderConfig(Sso, "sso", ForwardAuth: new ForwardAuthConfig("http://authelia:9091", ForwardAuthPreset.Authelia))],
        };
        Assert.Empty(ConfigValidator.Validate(document));

        var mapped = Map(document);
        var result = SnapshotCompiler.Compile(mapped.Model, new SnapshotCompilerOptions { ForwardAuth = new ForwardAuthRuntime(), RateLimitsWillBePublished = false });

        Assert.Empty(mapped.Errors);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        var route = Assert.Single(result.Snapshot!.HostsByIndex[0].Routes);
        Assert.IsType<ForwardAuthPolicy>(Assert.Single(route.Policies));
        Assert.Equal("sso", route.AuthProvider!.Name);
        Assert.Equal("sso", result.Snapshot.HostsByIndex[0].AuthProvider!.Name);
    }

    [Fact]
    public void AnAdminHostAndARedirectOrRespondHostIgnoreTheProviderAndSayWhy()
    {
        var document = Valid() with
        {
            AuthProviders = [new AuthProviderConfig(Sso, "sso", ForwardAuth: new ForwardAuthConfig("http://auth:9091", ForwardAuthPreset.Authelia))],
            Settings = new GlobalSettings(Defaults: new HostSettings { ForceHttps = true, AuthProvider = Use(Sso) }, TrustedProxies: new TrustedProxySettings(TrustedProxyMode.Generic, ["10.0.0.0/8"])),
        };
        var mapped = Map(document);

        Assert.All(mapped.Model.Hosts.Where(static h => h.Kind != HostKind.Proxy), host => Assert.Null(host.AuthProvider.Effective));
        var ignored = mapped.Warnings.Where(static w => w.Code == ConfigWarningCodes.AuthProviderIgnored).ToArray();
        Assert.Equal(document.Hosts.Count(static h => h.Kind != HostKind.Proxy && h.Enabled), ignored.Length);
        Assert.All(ignored, warning => Assert.Equal("/settings/authProvider", warning.Member));
    }

    [Fact]
    public void AHostThatAlsoAnswersPlainHttpIsWarnedAboutTheSessionCookie()
    {
        var plain = MapDocument(forceHttps: false);
        var secure = MapDocument(forceHttps: true);

        Assert.Contains(plain.Warnings, static w => w.Code == ConfigWarningCodes.AuthProviderInsecureSession);
        Assert.DoesNotContain(secure.Warnings, static w => w.Code == ConfigWarningCodes.AuthProviderInsecureSession);

        static MappedConfig MapDocument(bool forceHttps) => Map(Document(new HostSettings { ForceHttps = forceHttps, AuthProvider = Use(Sso) }, null, null) with
        {
            AuthProviders = [new AuthProviderConfig(Sso, "sso", ForwardAuth: new ForwardAuthConfig("http://auth:9091", ForwardAuthPreset.Authelia))],
        });
    }

    private static AuthProviderSpec MapProvider(ForwardAuthConfig forward) =>
        Assert.Single(Map(Document(null, null, null) with { AuthProviders = [new AuthProviderConfig(Sso, "sso", ForwardAuth: forward)] }).Model.AuthProviders);

    private static HostSpec MapHost(HostSettings? host, params RouteSettings[] routes)
    {
        var document = Document(null, null, host);
        var config = document.Hosts[0] with
        {
            Routes = [.. routes.Select((settings, i) => new RouteConfig(Id<RouteId>(100 + i), AppUpstream, Path: $"/r{i}", Settings: settings))],
        };
        return Assert.Single(Map(document with { Hosts = [config] }).Model.Hosts);
    }

    private static MappedConfig Map(ConfigDocument document) => TestMapping.Map(document, Revision, CertificateInventory.Empty);

    private static ConfigDocument Document(HostSettings? defaults, HostSettings? profile, HostSettings? host) => new(
        Settings: new GlobalSettings(Defaults: defaults),
        Profiles: profile is null ? [] : [new ProfileConfig(LanProfile, "profile", Settings: profile)],
        Upstreams: [new UpstreamConfig(AppUpstream, "app", Destinations: [new DestinationConfig(AppDestination, "http://app")])],
        Hosts: [new HostConfig(AppHost, ["app.example.com"], ProfileId: profile is null ? null : LanProfile, Settings: host, Routes: [new RouteConfig(RootRoute, AppUpstream)])],
        AuthProviders:
        [
            new AuthProviderConfig(Sso, "sso", ForwardAuth: new ForwardAuthConfig("http://auth:9091")),
            new AuthProviderConfig(Other, "other", ForwardAuth: new ForwardAuthConfig("http://other:9091")),
        ]);
}
