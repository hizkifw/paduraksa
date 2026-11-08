using System.Reflection;

using AngleSharp.Dom;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor;

using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The settings pages (PLAN §12, page 10): global defaults and profiles through the review drawer, users with the last-Owner guard's
/// refusal, API tokens whose secret is shown once and dropped, and what each role is offered.
/// </summary>
public sealed class SettingsPagesTests
{
    [Fact]
    public async Task ANewToken_ShowsItsSecretOnce_InItsDrawer_AndForgetsItWhenTheDrawerCloses()
    {
        await using DashboardTestContext context = new();
        (FakeDashboardIdentity identity, _, _, _) = IdentityPages.SignIn(context, Role.Operator);
        context.Render<MudPopoverProvider>();
        IRenderedComponent<DrawerHost> dialogs = context.Render<DrawerHost>();
        IRenderedComponent<ApiTokens> page = context.Render<ApiTokens>();
        await page.WaitForStateAsync(() => page.Markup.Contains("No API tokens yet", StringComparison.Ordinal));

        // The drawer holds the page's click until it closes.
        Task creating = page.Find("[data-action=add-token]").ClickAsync(new());
        await dialogs.WaitForStateAsync(() => dialogs.FindAll("[data-scope]").Count > 0);
        // Only what an Operator holds is offered, and never the token or session permissions.
        string[] offered = [.. dialogs.FindAll("[data-scope]").Select(e => e.GetAttribute("data-scope")!)];
        Assert.Contains("config:write", offered);
        Assert.DoesNotContain("users:write", offered);
        Assert.DoesNotContain("tokens:own", offered);
        Assert.Contains("never exceeds its owner", dialogs.Markup, StringComparison.Ordinal);

        await dialogs.Find("input[data-field=token-name]").InputAsync(new() { Value = "backup script" });
        await dialogs.Find("form[data-drawer=token]").SubmitAsync();

        await dialogs.WaitForStateAsync(() => dialogs.FindAll("[data-secret=token]").Count == 1);
        // Only "I've copied it" closes it now: no close button, and Escape does nothing.
        Assert.Empty(dialogs.FindAll("[data-drawer-action=close]"));
        await dialogs.InvokeAsync(dialogs.Instance.EscapeAsync);
        Assert.Single(dialogs.FindAll("[data-secret=token]"));
        Assert.Contains(FakeDashboardIdentity.TokenSecret, dialogs.Find("[data-secret=token]").TextContent, StringComparison.Ordinal);
        Assert.Contains("you won't see it again", dialogs.Find("[data-notice=once]").TextContent, StringComparison.OrdinalIgnoreCase);
        IssueApiTokenRequest request = Assert.Single(identity.Issued);
        Assert.Equal("backup script", request.Name);
        Assert.All(request.Scopes, scope => Assert.True(Paduraksa.Dashboard.Identity.IdentityText.IsRead(scope)));
        Assert.NotNull(request.ExpiresAt);

        Paduraksa.Dashboard.AccountArea.TokenDrawer drawer = dialogs.FindComponent<Paduraksa.Dashboard.AccountArea.TokenDrawer>().Instance;
        await dialogs.Find("[data-action=done]").ClickAsync(new());
        await creating.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);

        Assert.Empty(dialogs.FindAll(".pdk-sheet"));
        Assert.DoesNotContain(FakeDashboardIdentity.TokenSecret, dialogs.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeDashboardIdentity.TokenSecret, page.Markup, StringComparison.Ordinal);
        Assert.Null(Field(drawer, "_issued"));
        await page.WaitForStateAsync(() => page.FindAll("[data-token='backup script']").Count == 1);
    }

    [Fact]
    public async Task EveryonesTokens_AreReadInOneCall_WithTheirOwnersNames()
    {
        await using DashboardTestContext context = new();
        (FakeDashboardIdentity identity, _, _, DashboardPrincipalRef me) = SignInOwner(context);
        UserSummary grace = FakeDashboardIdentity.User("grace", Role.Operator) with { DisplayName = "Grace Hopper" };
        identity.Users.Add(grace);
        identity.Users.Add(FakeDashboardIdentity.User("linus", Role.Viewer));
        identity.Tokens.Add(Token(me.Id, "mine"));
        identity.Tokens.Add(Token(grace.Id, "graces"));
        context.Render<MudPopoverProvider>();
        IRenderedComponent<ApiTokens> page = context.Render<ApiTokens>();
        await page.WaitForStateAsync(() => page.FindAll("[data-token=mine]").Count == 1);

        await page.InvokeAsync(() => page.FindComponent<MudToggleGroup<bool>>().Instance.ValueChanged.InvokeAsync(true));

        await page.WaitForStateAsync(() => page.FindAll("[data-token=graces]").Count == 1);
        Assert.Contains("Grace Hopper", page.Find("[data-token=graces]").Closest("[role=row]")!.TextContent, StringComparison.Ordinal);
        Assert.Single(identity.Calls, call => call == "list all tokens");
    }

    [Fact]
    public async Task EditingTheLastOwner_ShowsTheGuardsRefusal()
    {
        await using DashboardTestContext context = new();
        (FakeDashboardIdentity identity, _, _, DashboardPrincipalRef me) = SignInOwner(context);
        identity.UpdateStatus = IdentityStatus.LastOwner;
        context.Render<MudPopoverProvider>();
        IRenderedComponent<DrawerHost> dialogs = context.Render<DrawerHost>();
        IRenderedComponent<Users> page = context.Render<Users>();
        await page.WaitForStateAsync(() => page.FindAll("[data-action=edit]").Count == 1);

        _ = page.Find("[data-action=edit]").ClickAsync(new());
        await dialogs.WaitForStateAsync(() => dialogs.FindAll("[data-role]").Count == 5);
        await dialogs.Find("[data-role=owner] input").ChangeAsync(new() { Value = false });
        await dialogs.Find("[data-role=admin] input").ChangeAsync(new() { Value = true });
        await dialogs.Find("form[data-drawer=user]").SubmitAsync();

        await dialogs.WaitForStateAsync(() => dialogs.FindAll("[data-refusal=last-owner]").Count == 1);
        Assert.Contains("at least one enabled Owner", dialogs.Find("[data-refusal=last-owner]").TextContent, StringComparison.Ordinal);
        (UserId id, UpdateUserRequest sent) = Assert.Single(identity.Updates);
        Assert.Equal(me.Id, id);
        Assert.Equal([Role.Admin], sent.Roles);
        Assert.Null(sent.DisplayName);
        Assert.Null(sent.Disabled);
    }

    [Fact]
    public async Task AUserDrawer_TypedBackToWhatItWas_HoldsNoChange()
    {
        await using DashboardTestContext context = new();
        SignInOwner(context);
        context.Render<MudPopoverProvider>();
        IRenderedComponent<DrawerHost> dialogs = context.Render<DrawerHost>();
        IRenderedComponent<Users> page = context.Render<Users>();
        await page.WaitForStateAsync(() => page.FindAll("[data-action=edit]").Count == 1);
        Task editing = page.Find("[data-action=edit]").ClickAsync(new());
        await dialogs.WaitForStateAsync(() => dialogs.FindAll("input[data-field=display-name]").Count == 1);
        string name = dialogs.Find("input[data-field=display-name]").GetAttribute("value")!;

        await dialogs.Find("input[data-field=display-name]").InputAsync(new() { Value = "Someone else" });
        await dialogs.Find("input[data-field=display-name]").InputAsync(new() { Value = name });
        await dialogs.InvokeAsync(dialogs.Instance.EscapeAsync);

        Assert.Empty(dialogs.FindAll("[data-drawer=discard]"));
        await editing.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
        Assert.Empty(dialogs.FindAll(".pdk-sheet"));
    }

    [Fact]
    public async Task AddingAUser_ChecksTheFormFirst_AndSendsTheRoles()
    {
        await using DashboardTestContext context = new();
        (FakeDashboardIdentity identity, _, _, _) = SignInOwner(context);
        context.Render<MudPopoverProvider>();
        IRenderedComponent<DrawerHost> dialogs = context.Render<DrawerHost>();
        IRenderedComponent<Users> page = context.Render<Users>();
        await page.WaitForStateAsync(() => page.FindAll("[data-action=add-user]").Count == 1);
        Assert.Contains("Reads the config and certificates and runs the simulator", page.Find("[data-section=roles]").TextContent, StringComparison.Ordinal);

        Task adding = page.Find("[data-action=add-user]").ClickAsync(new());
        await dialogs.WaitForStateAsync(() => dialogs.FindAll("input[data-field=username]").Count == 1);
        await dialogs.Find("input[data-field=username]").InputAsync(new() { Value = "bo" });
        await dialogs.Find("input[data-field=password]").InputAsync(new() { Value = "short" });
        await dialogs.Find("form[data-drawer=user]").SubmitAsync();
        Assert.Empty(identity.Created);
        Assert.Contains("3 to 64 characters.", dialogs.Markup, StringComparison.Ordinal);
        Assert.Contains("At least 12 characters.", dialogs.Markup, StringComparison.Ordinal);

        await dialogs.Find("input[data-field=username]").InputAsync(new() { Value = "viewer" });
        await dialogs.Find("input[data-field=password]").InputAsync(new() { Value = "a long enough password" });
        await dialogs.Find("form[data-drawer=user]").SubmitAsync();

        CreateUserRequest created = Assert.Single(identity.Created);
        Assert.Equal("viewer", created.Username);
        Assert.Equal([Role.Viewer], created.Roles);
        await adding.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
        Assert.Empty(dialogs.FindAll(".pdk-sheet"));
        await page.WaitForStateAsync(() => page.FindAll("[data-user=viewer]").Count == 1);
    }

    [Theory]
    [InlineData(Role.Owner, true, true)]
    [InlineData(Role.Admin, true, false)]
    [InlineData(Role.Viewer, false, false)]
    public async Task TheUsersPage_FollowsTheRole(Role role, bool listed, bool editable)
    {
        await using DashboardTestContext context = new();
        IdentityPages.SignIn(context, role);
        context.Render<MudPopoverProvider>();
        IRenderedComponent<Users> page = context.Render<Users>();

        await page.WaitForStateAsync(() => page.FindAll("[data-user]").Count == 1 || page.Markup.Contains("can't see the users", StringComparison.Ordinal));
        Assert.Equal(listed, page.FindAll("[data-user]").Count == 1);
        Assert.Equal(editable, page.FindAll("[data-action=add-user]").Count == 1);
        Assert.Equal(editable, page.FindAll("[data-action=edit]").Count == 1);
    }

    [Theory]
    [InlineData(Role.Operator, true)]
    [InlineData(Role.Viewer, false)]
    public async Task TheGlobalDefaults_AreEditableOnlyWithConfigWrite_AndGoThroughReview(Role role, bool writable)
    {
        await using DashboardTestContext context = new();
        (_, _, FakeDashboardConfig config, _) = IdentityPages.SignIn(context, role);
        config.Document = new ConfigDocument(Settings: new GlobalSettings(Version: 4));
        context.Render<MudPopoverProvider>();
        context.Render<Paduraksa.Dashboard.Drawers.DrawerHost>();
        IRenderedComponent<GlobalDefaults> page = context.Render<GlobalDefaults>();
        await page.WaitForStateAsync(() => page.FindAll("[data-form=defaults]").Count == 1);

        Assert.Equal(writable, page.FindAll("[data-action=review]").Count == 1);
        Assert.Equal(!writable, page.FindAll("[data-banner=read-only]").Count == 1);
        if (!writable)
        {
            return;
        }

        await page.Find("[data-mode=cloudflareproxy]").ClickAsync(new());
        await page.Find("[data-action=review]").ClickAsync(new());

        await page.WaitForStateAsync(() => config.Executed.Count == 1);
        UpdateSettingsCommand command = Assert.IsType<UpdateSettingsCommand>(config.Executed[0]);
        Assert.True(command.DryRun);
        Assert.Equal(4, command.Settings.Version);
        Assert.Equal(TrustedProxyMode.CloudflareProxy, command.Settings.TrustedProxies.Mode);
        Assert.Empty(command.Settings.TrustedProxies.Proxies);
    }

    [Fact]
    public async Task AProfileInUse_CannotBeDeleted_AndSaysWhichHosts()
    {
        await using DashboardTestContext context = new();
        (_, _, FakeDashboardConfig config, _) = IdentityPages.SignIn(context, Role.Operator);
        ProfileConfig lan = new(ProfileId.New(), "LAN only", 1, new HostSettings { ForceHttps = true });
        HostConfig host = new(HostId.New(), ["nas.home.test"], Version: 1, ProfileId: lan.Id, Kind: HostKind.Respond, Respond: new RespondConfig(200, "ok"));
        config.Document = new ConfigDocument(Profiles: [lan], Hosts: [host]);
        IRenderedComponent<Profiles> page = context.Render<Profiles>();
        await page.WaitForStateAsync(() => page.FindAll("[data-profile='LAN only']").Count == 1);
        Assert.Contains("Redirects to HTTPS", page.Find("[data-profile='LAN only']").TextContent, StringComparison.Ordinal);
        Assert.Contains("nas.home.test", page.Find("[data-profile='LAN only']").TextContent, StringComparison.Ordinal);

        await page.Find("[data-action=delete]").ClickAsync(new());

        Assert.Contains("still used by nas.home.test", page.Find("[data-banner=in-use]").TextContent, StringComparison.Ordinal);
        Assert.Empty(config.Executed);
    }

    private static (FakeDashboardIdentity, Paduraksa.Dashboard.Audit.IDashboardAudit, FakeDashboardConfig, DashboardPrincipalRef) SignInOwner(DashboardTestContext context)
    {
        (FakeDashboardIdentity identity, FakeDashboardAudit audit, FakeDashboardConfig config, Paduraksa.Dashboard.Auth.DashboardPrincipal user) = IdentityPages.SignIn(context, Role.Owner);
        return (identity, audit, config, new DashboardPrincipalRef(user.Session.Session.UserId));
    }

    private static ApiTokenSummary Token(UserId owner, string name) =>
        new(ApiTokenId.New(), owner, name, [Permission.ConfigRead], null, DateTimeOffset.UtcNow.AddDays(-1), null, false);

    private static object? Field(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);

    private sealed record DashboardPrincipalRef(UserId Id);
}
