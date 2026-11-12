using Bunit;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor;

using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The auth providers: a page that says what each one is and who asks it, an editor whose preset fills in what its project expects (and says what
/// each empty field will use), and create, edit and delete through the same review the other objects use. A provider in use offers no delete.
/// </summary>
public sealed class AuthProvidersTests
{
    private static readonly AuthProviderConfig Authelia = new(
        AuthProviderId.New(), "Authelia", Notes: "The household SSO", ForwardAuth: new ForwardAuthConfig("http://authelia:9091", ForwardAuthPreset.Authelia));

    private static readonly AuthProviderConfig Unused = new(
        AuthProviderId.New(), "Spare", ForwardAuth: new ForwardAuthConfig("http://auth.internal:8080/verify"));

    [Fact]
    public async Task ThePage_ListsEachProviderWithItsEndpointAndTheHostsAndRoutesThatAskIt()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        HostConfig host = new(
            HostId.New(), ["nas.home.test"], Settings: new HostSettings { AuthProvider = Use(Authelia.Id) },
            Routes: [new RouteConfig(RouteId.New(), UpstreamId.New(), Path: "/public", Settings: new RouteSettings { AuthProvider = Use(Unused.Id) })]);
        config.Document = new ConfigDocument(AuthProviders: [Authelia, Unused], Hosts: [host]);

        var page = context.Render<AuthProviders>();

        await page.WaitForStateAsync(() => page.FindAll("[data-auth-provider]").Count == 2);
        var authelia = page.Find("[data-auth-provider=Authelia]");
        Assert.Contains("Authelia at http://authelia:9091", authelia.TextContent, StringComparison.Ordinal);
        Assert.Contains("The household SSO", authelia.TextContent, StringComparison.Ordinal);
        Assert.Contains("1 host", authelia.TextContent, StringComparison.Ordinal);
        Assert.Contains("nas.home.test", authelia.TextContent, StringComparison.Ordinal);
        var spare = page.Find("[data-auth-provider=Spare]");
        Assert.Contains("Other forward-auth endpoint at http://auth.internal:8080", spare.TextContent, StringComparison.Ordinal);
        Assert.Contains("1 route", spare.TextContent, StringComparison.Ordinal);
        Assert.Contains("/public", spare.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProviderInUse_OffersNoDelete_AndAnUnusedOneDoes()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(AuthProviders: [Authelia, Unused], Hosts: [new HostConfig(HostId.New(), ["nas.home.test"], Settings: new HostSettings { AuthProvider = Use(Authelia.Id) })]);

        var page = context.Render<AuthProviders>();

        await page.WaitForStateAsync(() => page.FindAll("[data-auth-provider]").Count == 2);
        Assert.Empty(page.FindAll("[data-auth-provider=Authelia] [data-action=delete]"));
        Assert.Single(page.FindAll("[data-auth-provider=Spare] [data-action=delete]"));
    }

    [Fact]
    public async Task APageWithoutProviders_SaysWhatOneIsFor()
    {
        await using DashboardTestContext context = new();
        ConfigPages.SignIn(context, Role.Operator).Document = new ConfigDocument();

        var page = context.Render<AuthProviders>();

        await page.WaitForStateAsync(() => page.FindAll("[data-section=auth-providers] .pdk-empty").Count == 1);
        var text = page.Find(".pdk-empty").TextContent;
        Assert.Contains("No auth providers yet", text, StringComparison.Ordinal);
        Assert.Contains("authentik, Authelia, oauth2-proxy", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreatingAProvider_SendsThePresetAndTheAddressAndLeavesEveryOtherFieldToThePreset()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument();
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<AuthProviders>(parameters => parameters.Add(p => p.Key, DashboardRoutes.NewSegment));

        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=auth-provider] input[data-field=name]").Count == 1);
        await TypeAsync(drawers, "input[data-field=name]", "SSO");
        await ChoosePresetAsync(drawers, ForwardAuthPreset.Authelia);
        await TypeAsync(drawers, "input[data-field=url]", "http://authelia:9091");

        // The form says what the preset will use for what was left empty.
        var text = drawers.Find("[data-drawer=auth-provider]").TextContent;
        Assert.Contains("Asked at http://authelia:9091/api/authz/forward-auth", text, StringComparison.Ordinal);
        Assert.Contains("Empty: 5. A timeout is a 504.", text, StringComparison.Ordinal);
        Assert.Contains("Empty: 10; 0: never.", text, StringComparison.Ordinal);
        Assert.Contains("Remote-User", drawers.Find("[data-section=response-headers-default]").TextContent, StringComparison.Ordinal);

        var reviewing = drawers.Find("form[data-drawer=auth-provider]").SubmitAsync();
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=apply]").Count == 1);
        var dry = Assert.IsType<UpsertAuthProviderCommand>(config.Executed[0]);
        Assert.True(dry.DryRun);
        var sent = dry.AuthProvider;
        Assert.Equal(("SSO", 0L), (sent.Name, sent.Version));
        Assert.Equal(new ForwardAuthConfig("http://authelia:9091", ForwardAuthPreset.Authelia), sent.ForwardAuth);

        await drawers.Find("[data-review=apply]").ClickAsync(new());
        await reviewing.WaitAsync(TimeSpan.FromSeconds(15), Xunit.TestContext.Current.CancellationToken);
        Assert.False(Assert.IsType<UpsertAuthProviderCommand>(config.Executed[^1]).DryRun);
    }

    [Fact]
    public async Task ThePresetChangesTheHintTheHelpAndWhatEmptyMeans()
    {
        await using DashboardTestContext context = new();
        ConfigPages.SignIn(context, Role.Operator).Document = new ConfigDocument();
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<AuthProviders>(parameters => parameters.Add(p => p.Key, DashboardRoutes.NewSegment));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=auth-provider] input[data-field=url]").Count == 1);

        await ChoosePresetAsync(drawers, ForwardAuthPreset.OAuth2Proxy);

        Assert.Equal("http://oauth2-proxy:4180", drawers.Find("input[data-field=url]").GetAttribute("placeholder"));
        var text = drawers.Find("[data-drawer=auth-provider]").TextContent;
        Assert.Contains("/oauth2/auth", text, StringComparison.Ordinal);
        Assert.Contains("Empty: /oauth2/start, with rd= the original address.", text, StringComparison.Ordinal);
        Assert.Contains("X-Auth-Request-User", drawers.Find("[data-section=response-headers-default]").TextContent, StringComparison.Ordinal);

        await ChoosePresetAsync(drawers, ForwardAuthPreset.Custom);

        Assert.Empty(drawers.FindAll("[data-field=response-headers-default]"));
        Assert.Single(drawers.FindAll("textarea[data-field=response-headers]"));
        Assert.Contains("Empty: the provider's 401 or redirect is relayed as it is.", drawers.Find("[data-drawer=auth-provider]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyingNoPresetHeadersSendsTheListTheEditorShowsAndTheDefaultSendsNone()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        var stored = Authelia with { Version = 3 };
        config.Document = new ConfigDocument(AuthProviders: [stored]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<AuthProviders>(parameters => parameters.Add(p => p.Key, stored.Id.ToString()));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-section=response-headers-default]").Count == 1);

        await drawers.InvokeAsync(() => drawers.FindComponents<MudSwitch<bool>>().Single(s => s.Instance.Label?.StartsWith("Copy the headers", StringComparison.Ordinal) == true).Instance.ValueChanged.InvokeAsync(false));
        await TypeAsync(drawers, "textarea[data-field=response-headers]", "Remote-User\nRemote-Email");
        await TypeAsync(drawers, "textarea[data-field=request-headers]", "X-Tenant");

        _ = drawers.Find("form[data-drawer=auth-provider]").SubmitAsync();
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=apply]").Count == 1);
        var sent = Assert.IsType<UpsertAuthProviderCommand>(config.Executed[0]).AuthProvider;
        Assert.Equal((stored.Id, 3L), (sent.Id, sent.Version));
        Assert.Equal(["Remote-User", "Remote-Email"], sent.ForwardAuth!.ResponseHeaders);
        Assert.Equal(["X-Tenant"], sent.ForwardAuth.RequestHeaders);
    }

    [Fact]
    public async Task TheForm_SaysWhatIsWrongAtTheFields()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(AuthProviders: [Unused]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<AuthProviders>(parameters => parameters.Add(p => p.Key, DashboardRoutes.NewSegment));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=auth-provider] input[data-field=name]").Count == 1);
        await TypeAsync(drawers, "input[data-field=name]", "spare");
        await TypeAsync(drawers, "input[data-field=url]", "http://user:pw@auth/");
        await ChoosePresetAsync(drawers, ForwardAuthPreset.Custom);
        await TypeAsync(drawers, "textarea[data-field=response-headers]", "Host");

        _ = drawers.Find("form[data-drawer=auth-provider]").SubmitAsync();
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-form=errors]").Count == 1);

        var text = drawers.Find("[data-drawer=auth-provider]").TextContent;
        Assert.Contains("is already used", text, StringComparison.Ordinal);
        Assert.Contains("can't carry a user name or password", text, StringComparison.Ordinal);
        Assert.Contains("header the proxy sets itself", text, StringComparison.Ordinal);
        Assert.Empty(config.Executed);
    }

    [Fact]
    public async Task DeletingAnUnusedProvider_AsksFirstAndSendsTheStoredVersion()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        var stored = Unused with { Version = 4 };
        config.Document = new ConfigDocument(AuthProviders: [stored]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        var page = context.Render<AuthProviders>();

        await page.WaitForStateAsync(() => page.FindAll("[data-auth-provider] [data-action=delete]").Count == 1);
        var deleting = page.Find("[data-auth-provider] [data-action=delete]").ClickAsync(new());
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=apply]").Count == 1);

        var dry = Assert.IsType<DeleteAuthProviderCommand>(config.Executed[0]);
        Assert.True(dry.DryRun);
        Assert.Equal((stored.Id, 4L), (dry.AuthProviderId, dry.Version));

        await drawers.Find("[data-review=apply]").ClickAsync(new());
        await deleting.WaitAsync(TimeSpan.FromSeconds(15), Xunit.TestContext.Current.CancellationToken);
        await drawers.WaitForStateAsync(() => config.Executed.Count == 2);
        Assert.False(Assert.IsType<DeleteAuthProviderCommand>(config.Executed[1]).DryRun);
    }

    [Fact]
    public async Task AReadOnlyRole_SeesTheProviderButCannotChangeIt()
    {
        await using DashboardTestContext context = new();
        ConfigPages.SignIn(context, Role.Viewer).Document = new ConfigDocument(AuthProviders: [Authelia]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        var page = context.Render<AuthProviders>(parameters => parameters.Add(p => p.Key, Authelia.Id.ToString()));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-banner=read-only]").Count == 1);

        Assert.Empty(drawers.FindAll("[data-action=review]"));
        Assert.Empty(page.FindAll("[data-action=add-auth-provider]"));
        Assert.True(drawers.Find("input[data-field=url]").HasAttribute("readonly"));
    }

    private static AuthProviderSettings Use(AuthProviderId provider) => new() { AuthProviderId = provider };

    /// <summary>Types into a MudBlazor field and leaves it (which sends what was typed at once, instead of after the field's debounce).</summary>
    private static async Task TypeAsync(IRenderedComponent<DrawerHost> drawers, string selector, string value)
    {
        var field = $"[data-drawer=auth-provider] {selector}";
        await drawers.Find(field).InputAsync(new() { Value = value });
        await drawers.Find(field).BlurAsync(new());
    }

    private static Task ChoosePresetAsync(IRenderedComponent<DrawerHost> drawers, ForwardAuthPreset preset) =>
        drawers.InvokeAsync(() => drawers.FindComponents<MudSelect<ForwardAuthPreset>>().Single().Instance.ValueChanged.InvokeAsync(preset));
}
