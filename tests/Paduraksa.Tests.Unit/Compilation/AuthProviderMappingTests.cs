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

    [Fact]
    public void AProviderNamedAboveALayerThatSwitchesSignInOffIsWarnedAboutBecauseNothingAsksIt()
    {
        var offAbove = Map(Document(new HostSettings { AuthProvider = new AuthProviderSettings { Enabled = false } }, null, new HostSettings { AuthProvider = Use(Sso) }));
        var warning = Assert.Single(offAbove.Warnings, static w => w.Code == ConfigWarningCodes.AuthProviderNamedButOff);
        Assert.Equal("/settings/authProvider", warning.Member);
        Assert.Equal(CompileErrorSource.Host(AppHost), warning.Source);
        // The merge is what it was: the provider is named and stays off.
        Assert.Null(Assert.Single(offAbove.Model.Hosts).Routes[0].AuthProvider);

        // Named by the profile, the warning is about the profile (its settings are where the missing switch goes), not about the host that only uses it.
        var profileDocument = Document(new HostSettings { AuthProvider = new AuthProviderSettings { Enabled = false } }, new HostSettings { AuthProvider = Use(Sso) }, null);
        var viaProfile = Map(profileDocument);
        var profileWarning = Assert.Single(viaProfile.Warnings, static w => w.Code == ConfigWarningCodes.AuthProviderNamedButOff);
        Assert.Equal(CompileErrorSource.Profile(LanProfile), profileWarning.Source);
        Assert.Equal("/settings/authProvider", profileWarning.Member);
        Assert.Equal("/profiles/0/settings/authProvider", new CompileErrorPaths(profileDocument).Of(profileWarning.Source) + profileWarning.Member);
    }

    [Fact]
    public void AProfileThatNamesAProviderAboveOffDefaultsIsWarnedAboutOnceHoweverManyHostsUseItAndNotForAHostThatSwitchesItOn()
    {
        var document = Document(new HostSettings { AuthProvider = new AuthProviderSettings { Enabled = false } }, new HostSettings { AuthProvider = Use(Sso) }, null);
        var template = document.Hosts[0];
        var second = template with { Id = Id<HostId>(50), Names = ["second.example.com"] };
        var third = template with { Id = Id<HostId>(51), Names = ["third.example.com"] };
        var switchedOn = template with { Id = Id<HostId>(52), Names = ["on.example.com"], Settings = new HostSettings { AuthProvider = new AuthProviderSettings { Enabled = true } } };

        var warnings = Map(document with { Hosts = [template, second, third, switchedOn] }).Warnings.Where(static w => w.Code == ConfigWarningCodes.AuthProviderNamedButOff).ToArray();

        Assert.Equal(CompileErrorSource.Profile(LanProfile), Assert.Single(warnings).Source);
    }

    [Fact]
    public void AProviderKeptWhileOffByTheLayerThatNamesItOrAboveItIsNotWarnedAbout()
    {
        var sameLayer = Map(Document(null, null, new HostSettings { AuthProvider = new AuthProviderSettings { Enabled = false, AuthProviderId = Sso } }));
        var offAbove = Map(Document(null, new HostSettings { AuthProvider = Use(Sso) }, new HostSettings { AuthProvider = new AuthProviderSettings { Enabled = false } }));
        var on = Map(Document(new HostSettings { AuthProvider = new AuthProviderSettings { Enabled = false } }, null, new HostSettings { AuthProvider = new AuthProviderSettings { Enabled = true, AuthProviderId = Sso } }));
        var nothing = Map(Document(null, null, null));

        foreach (var mapped in new[] { sameLayer, offAbove, on, nothing })
        {
            Assert.DoesNotContain(mapped.Warnings, static w => w.Code == ConfigWarningCodes.AuthProviderNamedButOff);
        }
    }

    [Fact]
    public void ARouteThatNamesAProviderUnderAHostWithSignInOffIsWarnedAboutAndOneThatSwitchesItOnIsNot()
    {
        var document = Document(null, null, new HostSettings { AuthProvider = new AuthProviderSettings { Enabled = false } });
        var host = document.Hosts[0] with
        {
            Routes =
            [
                new RouteConfig(Id<RouteId>(100), AppUpstream, Path: "/named", Settings: new RouteSettings { AuthProvider = Use(Sso) }),
                new RouteConfig(Id<RouteId>(101), AppUpstream, Path: "/on", Settings: new RouteSettings { AuthProvider = new AuthProviderSettings { Enabled = true, AuthProviderId = Sso } }),
                new RouteConfig(Id<RouteId>(102), AppUpstream, Path: "/plain"),
            ],
        };

        var mapped = Map(document with { Hosts = [host] });

        var warning = Assert.Single(mapped.Warnings, static w => w.Code == ConfigWarningCodes.AuthProviderNamedButOff);
        Assert.Equal(CompileErrorSource.Route(AppHost, Id<RouteId>(100)), warning.Source);
        Assert.Equal("/settings/authProvider", warning.Member);
    }

    [Theory]
    [InlineData("/oauth2", false, false)]
    [InlineData("/oauth2/start", false, false)]
    [InlineData("/oauth2", true, true)]
    [InlineData("/", true, true)]
    [InlineData("/elsewhere", false, true)]
    public void ASignInPageOnTheHostNeedsARouteForItWithSignInOff(string path, bool guarded, bool warned)
    {
        var document = Map1(path, guarded);

        Assert.Equal(warned, document.Warnings.Any(static w => w.Code == ConfigWarningCodes.AuthProviderSignInUnreachable));

        static MappedConfig Map1(string path, bool guarded)
        {
            var doc = Document(new HostSettings { AuthProvider = Use(Sso) }, null, null) with
            {
                AuthProviders = [new AuthProviderConfig(Sso, "sso", ForwardAuth: new ForwardAuthConfig("http://oauth:4180", ForwardAuthPreset.OAuth2Proxy))],
            };
            var routes = new List<RouteConfig> { new(RootRoute, AppUpstream) };
            if (path != "/")
            {
                routes.Add(new RouteConfig(Id<RouteId>(100), AppUpstream, Path: path, Settings: guarded ? null : new RouteSettings { AuthProvider = new AuthProviderSettings { Enabled = false } }));
            }
            else if (!guarded)
            {
                routes[0] = routes[0] with { Settings = new RouteSettings { AuthProvider = new AuthProviderSettings { Enabled = false } } };
            }

            return Map(doc with { Hosts = [doc.Hosts[0] with { Routes = routes }] });
        }
    }

    [Fact]
    public void ASignInPageTheMoreSpecificRouteGuardsIsWarnedAboutEvenWhenAShorterOneIsOpen()
    {
        var doc = Document(new HostSettings { AuthProvider = Use(Sso) }, null, null) with
        {
            AuthProviders = [new AuthProviderConfig(Sso, "sso", ForwardAuth: new ForwardAuthConfig("http://oauth:4180", ForwardAuthPreset.OAuth2Proxy))],
        };
        var host = doc.Hosts[0] with
        {
            Routes =
            [
                new RouteConfig(RootRoute, AppUpstream, Settings: new RouteSettings { AuthProvider = new AuthProviderSettings { Enabled = false } }),
                new RouteConfig(Id<RouteId>(100), AppUpstream, Path: "/oauth2/start", Match: RouteMatchKind.Exact),
            ],
        };

        Assert.Contains(Map(doc with { Hosts = [host] }).Warnings, static w => w.Code == ConfigWarningCodes.AuthProviderSignInUnreachable);
    }

    [Fact]
    public void ASignInPageOnAnotherHostOrNoneNeedsNoRoute()
    {
        var absolute = Document(new HostSettings { AuthProvider = Use(Sso) }, null, null) with
        {
            AuthProviders = [new AuthProviderConfig(Sso, "sso", ForwardAuth: new ForwardAuthConfig("http://auth:9091", ForwardAuthPreset.Authentik, SignIn: new ForwardAuthSignInConfig("https://login.example.com/start")))],
        };
        var none = Document(new HostSettings { AuthProvider = Use(Sso) }, null, null);

        Assert.DoesNotContain(Map(absolute).Warnings, static w => w.Code == ConfigWarningCodes.AuthProviderSignInUnreachable);
        Assert.DoesNotContain(Map(none).Warnings, static w => w.Code == ConfigWarningCodes.AuthProviderSignInUnreachable);
    }

    [Theory]
    [InlineData("https://app.example.com/oauth2/start", "/oauth2/start", false, true)]
    [InlineData("https://APP.example.com/oauth2/start?x=1", "/oauth2/start", false, true)]
    [InlineData("https://app.example.com/oauth2/start", "/oauth2", true, false)]
    [InlineData("https://other.example.com/oauth2/start", "/elsewhere", false, false)]
    [InlineData("https://sub.example.org/start", "/elsewhere", false, true)]
    public void AnAbsoluteSignInPageOnThisHostNeedsARouteForItWithSignInOffJustLikeARelativeOne(string signIn, string routePath, bool open, bool warned)
    {
        var doc = Document(new HostSettings { AuthProvider = Use(Sso) }, null, null) with
        {
            AuthProviders = [new AuthProviderConfig(Sso, "sso", ForwardAuth: new ForwardAuthConfig("http://auth:9091", ForwardAuthPreset.Authentik, SignIn: new ForwardAuthSignInConfig(signIn)))],
        };
        var host = doc.Hosts[0] with
        {
            Names = ["app.example.com", "*.example.org"],
            Routes =
            [
                new RouteConfig(RootRoute, AppUpstream),
                new RouteConfig(Id<RouteId>(100), AppUpstream, Path: routePath, Settings: open ? new RouteSettings { AuthProvider = new AuthProviderSettings { Enabled = false } } : null),
            ],
        };

        var mapped = Map(doc with { Hosts = [host] });

        Assert.Equal(warned, mapped.Warnings.Any(static w => w.Code == ConfigWarningCodes.AuthProviderSignInUnreachable));
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
