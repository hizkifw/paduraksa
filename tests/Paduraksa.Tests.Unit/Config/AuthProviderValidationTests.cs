using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

using static Paduraksa.Application.Config.ConfigErrorCodes;
using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Config;

/// <summary>An auth provider's endpoint and header lists, and the references to it. A document that validates always compiles: the
/// validator and the compiler read the same URL and header rules.</summary>
public sealed class AuthProviderValidationTests
{
    private const string Proxy = "/hosts/0";
    private const string ProviderPath = "/authProviders/0";
    private const string ForwardPath = "/authProviders/0/forwardAuth";

    [Fact]
    public void AProviderForEachPresetAndACustomOneAreValid()
    {
        Assert.Empty(ConfigValidator.Validate(WithProvider(new ForwardAuthConfig("http://authelia:9091", ForwardAuthPreset.Authelia))));
        Assert.Empty(ConfigValidator.Validate(WithProvider(new ForwardAuthConfig("http://authentik:9000", ForwardAuthPreset.Authentik))));
        Assert.Empty(ConfigValidator.Validate(WithProvider(new ForwardAuthConfig("http://oauth2-proxy:4180", ForwardAuthPreset.OAuth2Proxy))));
        Assert.Empty(ConfigValidator.Validate(WithProvider(new ForwardAuthConfig(
            "https://auth.example.com/verify?mode=forward",
            TimeoutSeconds: 30,
            CacheSeconds: 0,
            CacheScope: ForwardAuthCacheScope.Host,
            RequestHeaders: ["X-Tenant"],
            ResponseHeaders: ["X-User", "X-Groups", "X-Auth-*"],
            SignIn: new ForwardAuthSignInConfig("https://login.example.com/start?client=web", "next")))));
        Assert.Empty(ConfigValidator.Validate(WithProvider(new ForwardAuthConfig("http://auth/", ResponseHeaders: []))));
    }

    [Fact]
    public void AProviderNeedsItsEndpointBlock() =>
        AssertErrors(Valid() with { AuthProviders = [new AuthProviderConfig(Sso, "sso")] }, ($"{ProviderPath}/forwardAuth", AuthProviderForwardAuthRequired));

    [Theory]
    [InlineData("")]
    [InlineData("authelia:9091")]
    [InlineData("/api/authz")]
    [InlineData("ftp://auth.example.com/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://user:secret@auth.example.com/")]
    [InlineData("http://auth.example.com/#frag")]
    [InlineData("http:///path")]
    public void TheEndpointMustBeAnAbsoluteHttpOrHttpsUrlWithNoCredentials(string url) =>
        AssertErrors(WithProvider(new ForwardAuthConfig(url)), ($"{ForwardPath}/url", AuthProviderUrlInvalid));

    [Fact]
    public void TheEndpointHasAtMostTheLimitOfCharacters() =>
        AssertErrors(
            WithProvider(new ForwardAuthConfig("http://auth.example.com/" + new string('a', ForwardAuthLimits.MaxUrlLength))),
            ($"{ForwardPath}/url", AuthProviderUrlInvalid));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(31)]
    public void TheTimeoutIsOneToThirtySeconds(int seconds) =>
        AssertErrors(WithProvider(new ForwardAuthConfig("http://auth/", TimeoutSeconds: seconds)), ($"{ForwardPath}/timeoutSeconds", AuthProviderTimeoutInvalid));

    [Theory]
    [InlineData(-1)]
    [InlineData(301)]
    public void TheCacheTimeIsZeroToThreeHundredSeconds(int seconds) =>
        AssertErrors(WithProvider(new ForwardAuthConfig("http://auth/", CacheSeconds: seconds)), ($"{ForwardPath}/cacheSeconds", AuthProviderCacheSecondsInvalid));

    [Fact]
    public void AnUndefinedPresetOrScopeIsRefused()
    {
        AssertErrors(WithProvider(new ForwardAuthConfig("http://auth/", (ForwardAuthPreset)99)), ($"{ForwardPath}/preset", EnumUndefined));
        AssertErrors(WithProvider(new ForwardAuthConfig("http://auth/", CacheScope: (ForwardAuthCacheScope)99)), ($"{ForwardPath}/cacheScope", EnumUndefined));
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("Content-Length")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Connection")]
    [InlineData("x-forwarded-for")]
    [InlineData("X-Forwarded-Uri")]
    [InlineData("X-Real-IP")]
    [InlineData("X-Request-Id")]
    [InlineData("X_Real_IP")]
    [InlineData("X_Forwarded_For")]
    [InlineData("Forwarded")]
    [InlineData("a b")]
    [InlineData("name:")]
    [InlineData("")]
    [InlineData("X-*")]
    public void AResponseHeaderThatTheProxyOwnsOrThatIsntAHeaderNameIsRefused(string name) =>
        AssertErrors(WithProvider(new ForwardAuthConfig("http://auth/", ResponseHeaders: [name])), ($"{ForwardPath}/responseHeaders/0", AuthProviderHeaderInvalid));

    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("Cookie")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Auth*")]
    [InlineData("Coo*")]
    [InlineData("Proxy-Auth*")]
    [InlineData("Proxy_Authorization")]
    [InlineData("Proxy_Auth*")]
    public void AResponseHeaderThatIsTheClientsOwnCredentialIsRefusedWithItsOwnCodeSoItIsNeverRemovedFromTheHostsRequests(string name) =>
        AssertErrors(WithProvider(new ForwardAuthConfig("http://auth/", ResponseHeaders: [name])), ($"{ForwardPath}/responseHeaders/0", AuthProviderHeaderCredential));

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Cookie")]
    [InlineData("Proxy-Authorization")]
    public void TheProviderIsStillToldTheClientsCredentialsAndNoPresetCopiesThemUpstream(string name)
    {
        Assert.Empty(ConfigValidator.Validate(WithProvider(new ForwardAuthConfig("http://auth/", RequestHeaders: [name]))));
        foreach (var preset in Enum.GetValues<ForwardAuthPreset>())
        {
            Assert.DoesNotContain(ForwardAuthPresets.DefaultResponseHeaders(preset), listed => ForwardAuthHeaderNames.NamesClientCredential(listed));
            Assert.Empty(ConfigValidator.Validate(WithProvider(new ForwardAuthConfig("http://auth/", preset))));
        }
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("Content-Length")]
    [InlineData("X-Forwarded-Host")]
    [InlineData("X-*")]
    [InlineData("")]
    public void ARequestHeaderThatTheProxyOwnsIsRefusedAndNoRequestHeaderIsAPrefix(string name) =>
        AssertErrors(WithProvider(new ForwardAuthConfig("http://auth/", RequestHeaders: [name])), ($"{ForwardPath}/requestHeaders/0", AuthProviderHeaderInvalid));

    [Fact]
    public void AtMostTheLimitOfHeaderNamesAreListed()
    {
        var names = Enumerable.Range(0, ForwardAuthLimits.MaxHeaderNames + 1).Select(i => $"X-Name-{i}").ToArray();

        AssertErrors(WithProvider(new ForwardAuthConfig("http://auth/", ResponseHeaders: names)), ($"{ForwardPath}/responseHeaders", AuthProviderHeadersTooMany));
        AssertErrors(WithProvider(new ForwardAuthConfig("http://auth/", RequestHeaders: names)), ($"{ForwardPath}/requestHeaders", AuthProviderHeadersTooMany));
    }

    [Theory]
    [InlineData("oauth2/start")]
    [InlineData("//evil.example.com/x")]
    [InlineData("/\\evil.example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:pw@login.example.com/")]
    [InlineData("/start#frag")]
    [InlineData("/white space")]
    [InlineData("")]
    public void TheSignInUrlIsAPathOnTheHostOrAnAbsoluteHttpUrl(string url) =>
        AssertErrors(WithProvider(new ForwardAuthConfig("http://auth/", SignIn: new ForwardAuthSignInConfig(url))), ($"{ForwardPath}/signIn/url", AuthProviderSignInInvalid));

    [Theory]
    [InlineData("")]
    [InlineData("a&b")]
    [InlineData("a=b")]
    [InlineData("é")]
    public void TheReturnParameterIsPlainCharacters(string name) =>
        AssertErrors(
            WithProvider(new ForwardAuthConfig("http://auth/", SignIn: new ForwardAuthSignInConfig("/start", name))),
            ($"{ForwardPath}/signIn/returnParameter", AuthProviderReturnParameterInvalid));

    [Fact]
    public void ANameIsRequiredAndNamesAndIdsAreUnique()
    {
        AssertErrors(Valid() with { AuthProviders = [new AuthProviderConfig(Sso, "  ", ForwardAuth: Endpoint)] }, ($"{ProviderPath}/name", NameRequired));
        AssertErrors(
            Valid() with { AuthProviders = [new AuthProviderConfig(Sso, "SSO", ForwardAuth: Endpoint), new AuthProviderConfig(Id<AuthProviderId>(2), "sso", ForwardAuth: Endpoint)] },
            ("/authProviders/1/name", NameDuplicate));
        AssertErrors(
            Valid() with { AuthProviders = [new AuthProviderConfig(Sso, "one", ForwardAuth: Endpoint), new AuthProviderConfig(Sso, "two", ForwardAuth: Endpoint)] },
            ("/authProviders/1/id", IdDuplicate));
    }

    [Fact]
    public void AnUnknownHostProviderIsReportedWhereTheHostResolvesIt() =>
        AssertErrors(
            Valid() with
            {
                Hosts = MapHost(Valid(), AppHost, h => h with { Settings = new HostSettings { AuthProvider = new AuthProviderSettings { AuthProviderId = Id<AuthProviderId>(9) } } }),
            },
            ($"{Proxy}/settings/authProvider/authProviderId", HostAuthProviderUnknown));

    [Fact]
    public void AnUnknownRouteProviderIsReportedAtTheRoute() =>
        AssertErrors(
            Valid() with
            {
                Hosts = MapHost(Valid(), AppHost, h => h with
                {
                    Routes = [h.Routes[0] with { Settings = new RouteSettings { AuthProvider = new AuthProviderSettings { AuthProviderId = Id<AuthProviderId>(9) } } }, h.Routes[1]],
                }),
            },
            ($"{Proxy}/routes/0/settings/authProvider/authProviderId", RouteAuthProviderUnknown));

    [Fact]
    public void AProviderTheDefaultsNameIsReportedAtEachHostThatInheritsItWithTheLayer()
    {
        var document = Valid() with
        {
            Settings = new GlobalSettings(Defaults: new HostSettings { AuthProvider = new AuthProviderSettings { AuthProviderId = Id<AuthProviderId>(9) } }),
            Profiles = [],
            Hosts = [.. Valid().Hosts.Select(h => h with { ProfileId = null })],
        };

        var errors = ConfigValidator.Validate(document);

        Assert.Equal(
            ["/hosts/0/settings/authProvider/authProviderId", "/hosts/1/settings/authProvider/authProviderId", "/hosts/2/settings/authProvider/authProviderId"],
            errors.Select(e => e.Path));
        Assert.All(errors, e => Assert.Contains("global defaults", e.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void ANamedProviderIsCheckedEvenWhileTheSwitchIsOff() =>
        AssertErrors(
            Valid() with
            {
                Hosts = MapHost(Valid(), AppHost, h => h with { Settings = new HostSettings { AuthProvider = new AuthProviderSettings { Enabled = false, AuthProviderId = Id<AuthProviderId>(9) } } }),
            },
            ($"{Proxy}/settings/authProvider/authProviderId", HostAuthProviderUnknown));

    [Fact]
    public void ADocumentsProviderIsAValidReferenceInEveryLayer()
    {
        var document = Valid() with
        {
            AuthProviders = [new AuthProviderConfig(Sso, "sso", ForwardAuth: Endpoint)],
            Settings = new GlobalSettings(Defaults: new HostSettings { AuthProvider = new AuthProviderSettings { AuthProviderId = Sso } }),
            Profiles = [new ProfileConfig(LanProfile, "LAN only", Settings: new HostSettings { AuthProvider = new AuthProviderSettings { Enabled = false } })],
            Hosts = MapHost(
                Valid(),
                AppHost,
                h => h with
                {
                    Settings = new HostSettings { AuthProvider = new AuthProviderSettings { AuthProviderId = Sso } },
                    Routes = [h.Routes[0] with { Settings = new RouteSettings { AuthProvider = new AuthProviderSettings { Enabled = false } } }, h.Routes[1]],
                }),
        };

        Assert.Empty(ConfigValidator.Validate(document));
    }

    private static readonly ForwardAuthConfig Endpoint = new("http://auth:9091", ForwardAuthPreset.Authelia);

    private static ConfigDocument WithProvider(ForwardAuthConfig forward) =>
        Valid() with { AuthProviders = [new AuthProviderConfig(Sso, "sso", ForwardAuth: forward)] };

    private static void AssertErrors(ConfigDocument document, params (string Path, string Code)[] expected) =>
        Assert.Equal(expected, ConfigValidator.Validate(document).Select(e => (e.Path, e.Code)));

    private static HostConfig[] MapHost(ConfigDocument document, HostId id, Func<HostConfig, HostConfig> change) =>
        [.. document.Hosts.Select(h => h.Id == id ? change(h) : h)];
}
