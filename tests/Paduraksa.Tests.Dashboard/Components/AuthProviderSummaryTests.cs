using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Config;
using Paduraksa.Dashboard.SettingsArea;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>What a layer's auth-provider record says in its card's summary line, naming the provider rather than its ID, and what a provider and
/// its usage say in a list.</summary>
public sealed class AuthProviderSummaryTests
{
    private static readonly AuthProviderConfig Sso = new(AuthProviderId.New(), "SSO", ForwardAuth: new ForwardAuthConfig("http://authelia:9091/api/authz", ForwardAuthPreset.Authelia));

    [Fact]
    public void ALayerSaysWhichProviderItNamesOffOrThatItInheritsOne()
    {
        var document = new ConfigDocument(AuthProviders: [Sso]);

        Assert.Equal(["Sign-in: SSO"], SettingsSummary.Summary(new HostSettings { AuthProvider = new AuthProviderSettings { AuthProviderId = Sso.Id } }, document));
        Assert.Equal(["Sign-in off"], SettingsSummary.Summary(new HostSettings { AuthProvider = new AuthProviderSettings { Enabled = false, AuthProviderId = Sso.Id } }, document));
        Assert.Equal(["Sign-in: the inherited provider"], SettingsSummary.Summary(new HostSettings { AuthProvider = new AuthProviderSettings { Enabled = true } }, document));
        Assert.Equal(["Sign-in: a provider"], SettingsSummary.Summary(new HostSettings { AuthProvider = new AuthProviderSettings { AuthProviderId = AuthProviderId.New() } }, document));
        Assert.Empty(SettingsSummary.Summary(new HostSettings()));
    }

    [Fact]
    public void AProviderIsSummarisedByItsPresetAndTheAddressWithoutThePath()
    {
        Assert.Equal("Authelia at http://authelia:9091", AuthProviderText.Summary(Sso));
        Assert.Equal("Other forward-auth endpoint at http://auth.internal:8080", AuthProviderText.Summary(new AuthProviderConfig(AuthProviderId.New(), "x", ForwardAuth: new ForwardAuthConfig("http://auth.internal:8080/verify"))));
        Assert.Equal("no endpoint", AuthProviderText.Summary(new AuthProviderConfig(AuthProviderId.New(), "x")));
    }

    [Theory]
    [InlineData(1, 0, "1 host", "uses")]
    [InlineData(0, 1, "1 route", "uses")]
    [InlineData(2, 0, "2 hosts", "use")]
    [InlineData(1, 1, "1 host, 1 route", "use")]
    public void TheVerbAgreesWithTheSubjectTheCountTextBuilds(int hosts, int routes, string text, string verb)
    {
        var host = new HostConfig(HostId.New(), ["app.example.com"]);
        var route = new AuthProviderRouteUse(host, new RouteConfig(RouteId.New(), UpstreamId.New(), Path: "/admin"));
        AuthProviderUsage usage = new([.. Enumerable.Repeat(host, hosts)], [.. Enumerable.Repeat(route, routes)]);

        Assert.Equal(text, usage.CountText);
        Assert.Equal(verb, usage.UseVerb);
    }

    [Fact]
    public void AProviderTheDefaultsNameIsInUseByEveryHostThatInheritsItAndARouteThatNamesItIsListed()
    {
        var upstream = UpstreamId.New();
        HostConfig inheriting = new(HostId.New(), ["a.example.com"], Routes: [new RouteConfig(RouteId.New(), upstream)]);
        HostConfig turnedOff = new(HostId.New(), ["b.example.com"], Settings: new HostSettings { AuthProvider = new AuthProviderSettings { Enabled = false, AuthProviderId = Sso.Id } }, Routes: [new RouteConfig(RouteId.New(), upstream)]);
        var routeId = RouteId.New();
        HostConfig other = new(
            HostId.New(),
            ["c.example.com"],
            Settings: new HostSettings { AuthProvider = new AuthProviderSettings { AuthProviderId = AuthProviderId.New() } },
            Routes: [new RouteConfig(routeId, upstream, Path: "/admin", Settings: new RouteSettings { AuthProvider = new AuthProviderSettings { AuthProviderId = Sso.Id } })]);
        var document = new ConfigDocument(
            Settings: new GlobalSettings(Defaults: new HostSettings { AuthProvider = new AuthProviderSettings { AuthProviderId = Sso.Id } }),
            AuthProviders: [Sso],
            Hosts: [inheriting, turnedOff, other]);

        var usage = AuthProviderUsageOf.Find(document, Sso.Id);

        // The host that turns the switch off still names the provider (a reference kept while off), so deleting it would be refused too.
        Assert.Equal(["a.example.com", "b.example.com"], usage.Hosts.Select(h => h.Names[0]));
        Assert.Equal(routeId, Assert.Single(usage.Routes).Route.Id);
        Assert.False(usage.IsEmpty);
    }
}
