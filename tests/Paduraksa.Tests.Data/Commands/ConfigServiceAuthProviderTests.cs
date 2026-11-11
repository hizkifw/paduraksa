using Paduraksa.Application.Config;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using static Paduraksa.Tests.Data.Commands.Objects;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Commands;

/// <summary>Auth providers through the command pipeline: stored canonically, versioned and audited like the other objects, every reference
/// resolved, and a provider that something names can't be deleted.</summary>
public sealed class ConfigServiceAuthProviderTests : IAsyncLifetime
{
    private ConfigServiceHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await ConfigServiceHarness.CreateAsync();

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    private static AuthProviderConfig Provider(AuthProviderId id, string name, ForwardAuthConfig? forward = null) =>
        new(id, name, ForwardAuth: forward ?? new ForwardAuthConfig("http://authelia:9091", ForwardAuthPreset.Authelia));

    private static HostSettings Names(AuthProviderId id) => new() { AuthProvider = new AuthProviderSettings { AuthProviderId = id } };

    [Fact]
    public async Task AProviderIsStoredCanonicallyVersionedAndAudited()
    {
        var id = AuthProviderId.New();
        var messy = Provider(id, "  Authelia  ", new ForwardAuthConfig(" http://authelia:9091 ", ForwardAuthPreset.Authelia, RequestHeaders: ["x-tenant", "X-Tenant", " Accept-Charset"]));

        var created = await _h.RunAsync(new UpsertAuthProviderCommand(messy));

        Assert.Equal(ConfigCommandStatus.Committed, created.Status);
        var stored = Assert.Single((await _h.CurrentAsync()).AuthProviders);
        Assert.Equal("Authelia", stored.Name);
        Assert.Equal("http://authelia:9091", stored.ForwardAuth!.Url);
        Assert.Equal(["Accept-Charset", "x-tenant"], stored.ForwardAuth.RequestHeaders);
        var createdVersion = stored.Version;
        Assert.True(createdVersion > 0);
        Assert.Equal("config.auth_provider.create", (await _h.WaitForAuditAsync(e => e.Action == "config.auth_provider.create")).Action);

        var changed = await _h.RunAsync(new UpsertAuthProviderCommand(stored with { ForwardAuth = stored.ForwardAuth with { CacheSeconds = 30 } }));

        Assert.Equal(ConfigCommandStatus.Committed, changed.Status);
        Assert.True(Assert.Single((await _h.CurrentAsync()).AuthProviders).Version > createdVersion);
        Assert.Equal("config.auth_provider.update", (await _h.WaitForAuditAsync(e => e.Action == "config.auth_provider.update")).Action);

        var stale = await _h.RunAsync(new UpsertAuthProviderCommand(stored with { Notes = "late" }));
        Assert.Equal(ConfigCommandStatus.Conflict, stale.Status);
        await _h.AssertChainIntactAsync();
    }

    [Fact]
    public async Task AProviderThatCantBeUsedIsRefusedWithThePointerOfTheFieldInsideIt()
    {
        var bad = Provider(AuthProviderId.New(), "bad", new ForwardAuthConfig("ftp://auth", TimeoutSeconds: 99, ResponseHeaders: ["Host"]));

        var result = await _h.RunAsync(new UpsertAuthProviderCommand(bad));

        Assert.Equal(ConfigCommandStatus.Invalid, result.Status);
        Assert.Equal(
            [("/forwardAuth/url", ConfigErrorCodes.AuthProviderUrlInvalid), ("/forwardAuth/timeoutSeconds", ConfigErrorCodes.AuthProviderTimeoutInvalid), ("/forwardAuth/responseHeaders/0", ConfigErrorCodes.AuthProviderHeaderInvalid)],
            result.Errors.Select(e => (e.Path, e.Code)));
        Assert.Empty((await _h.CurrentAsync()).AuthProviders);
    }

    [Fact]
    public async Task AHostCantNameAProviderThatDoesntExist()
    {
        var (upstream, host) = (UpstreamId.New(), HostId.New());
        await _h.RunAsync(new UpsertUpstreamCommand(Upstream(upstream, "app")));

        var refused = await _h.RunAsync(new UpsertHostCommand(ProxyHost(host, "app.example.com", upstream) with { Settings = Names(AuthProviderId.New()) }));

        Assert.Equal(ConfigCommandStatus.Invalid, refused.Status);
        Assert.Equal(ConfigErrorCodes.HostAuthProviderUnknown, Assert.Single(refused.Errors).Code);
    }

    [Fact]
    public async Task AProviderInUseCantBeDeletedAndAnUnusedOneCan()
    {
        var (provider, upstream, host) = (AuthProviderId.New(), UpstreamId.New(), HostId.New());
        await _h.RunAsync(new UpsertAuthProviderCommand(Provider(provider, "sso")));
        await _h.RunAsync(new UpsertUpstreamCommand(Upstream(upstream, "app")));
        await _h.RunAsync(new UpsertHostCommand(ProxyHost(host, "app.example.com", upstream) with { Settings = Names(provider) }));
        var version = Assert.Single((await _h.CurrentAsync()).AuthProviders).Version;

        var refused = await _h.RunAsync(new DeleteAuthProviderCommand(provider, version));

        Assert.Equal(ConfigCommandStatus.Invalid, refused.Status);
        Assert.Contains(refused.RelatedErrors, e => e.Code == ConfigErrorCodes.HostAuthProviderUnknown);
        Assert.Single((await _h.CurrentAsync()).AuthProviders);

        var hostVersion = Assert.Single((await _h.CurrentAsync()).Hosts).Version;
        await _h.RunAsync(new UpsertHostCommand(ProxyHost(host, "app.example.com", upstream) with { Version = hostVersion }));
        var deleted = await _h.RunAsync(new DeleteAuthProviderCommand(provider, version));

        Assert.Equal(ConfigCommandStatus.Committed, deleted.Status);
        Assert.Empty((await _h.CurrentAsync()).AuthProviders);
        Assert.Equal("config.auth_provider.delete", (await _h.WaitForAuditAsync(e => e.Action == "config.auth_provider.delete")).Action);
    }

    [Fact]
    public async Task AProviderAHostReachesThroughItsProfileTheDefaultsOrARouteCantBeDeleted()
    {
        var (provider, upstream, profile, host, route) = (AuthProviderId.New(), UpstreamId.New(), ProfileId.New(), HostId.New(), RouteId.New());
        await _h.RunAsync(new UpsertAuthProviderCommand(Provider(provider, "sso")));
        await _h.RunAsync(new UpsertUpstreamCommand(Upstream(upstream, "app")));
        var version = Assert.Single((await _h.CurrentAsync()).AuthProviders).Version;

        await _h.RunAsync(new UpsertProfileCommand(Profile(profile, "lan") with { Settings = Names(provider) }));
        await _h.RunAsync(new UpsertHostCommand(ProxyHost(host, "app.example.com", upstream) with { ProfileId = profile }));
        var viaProfile = await _h.RunAsync(new DeleteAuthProviderCommand(provider, version));
        Assert.Equal(ConfigCommandStatus.Invalid, viaProfile.Status);
        Assert.Contains("inherited from its profile", Assert.Single(viaProfile.RelatedErrors).Message, StringComparison.Ordinal);

        var current = await _h.CurrentAsync();
        await _h.RunAsync(new UpsertHostCommand(ProxyHost(host, "app.example.com", upstream) with { Version = current.Hosts[0].Version }));
        await _h.RunAsync(new UpdateSettingsCommand(current.Settings with { Defaults = Names(provider) }));
        var viaDefaults = await _h.RunAsync(new DeleteAuthProviderCommand(provider, version));
        Assert.Equal(ConfigCommandStatus.Invalid, viaDefaults.Status);
        Assert.Contains("inherited from the global defaults", Assert.Single(viaDefaults.RelatedErrors).Message, StringComparison.Ordinal);

        await _h.RunAsync(new UpdateSettingsCommand((await _h.CurrentAsync()).Settings with { Defaults = new HostSettings() }));
        var withRoute = ProxyHost(host, "app.example.com", upstream, route);
        await _h.RunAsync(new UpsertHostCommand(withRoute with
        {
            Version = (await _h.CurrentAsync()).Hosts[0].Version,
            Routes = [withRoute.Routes[0] with { Settings = new RouteSettings { AuthProvider = new AuthProviderSettings { AuthProviderId = provider } } }],
        }));
        var viaRoute = await _h.RunAsync(new DeleteAuthProviderCommand(provider, version));
        Assert.Equal(ConfigCommandStatus.Invalid, viaRoute.Status);
        Assert.Equal(ConfigErrorCodes.RouteAuthProviderUnknown, Assert.Single(viaRoute.RelatedErrors).Code);
    }

    [Fact]
    public async Task AProviderAHostUsesCanBeRenamedAndItsNotesChangedAndTheInsecureSessionWarningNeverBlocksIt()
    {
        var (provider, upstream, host) = (AuthProviderId.New(), UpstreamId.New(), HostId.New());
        await _h.RunAsync(new UpsertAuthProviderCommand(Provider(provider, "sso")));
        await _h.RunAsync(new UpsertUpstreamCommand(Upstream(upstream, "app")));
        await _h.RunAsync(new UpsertHostCommand(ProxyHost(host, "app.example.com", upstream) with { Settings = Names(provider) }));

        var stored = Assert.Single((await _h.CurrentAsync()).AuthProviders);
        var result = await _h.RunAsync(new UpsertAuthProviderCommand(stored with { Name = "renamed", Notes = "a note" }));

        // The host doesn't force HTTPS, so the insecure-session warning applies; a warning is information, never a refusal.
        Assert.Equal(ConfigCommandStatus.Committed, result.Status);
    }
}
