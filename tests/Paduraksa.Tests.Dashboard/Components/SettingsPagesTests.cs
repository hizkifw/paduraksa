using System.Reflection;
using Bunit;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor;

using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Components;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The settings pages: global defaults and profiles through the review drawer, users with the last-Owner guard's
/// refusal, API tokens whose secret is shown once and dropped, and what each role is offered.
/// </summary>
public sealed class SettingsPagesTests
{
    [Fact]
    public async Task TheTokensPageShowsTheMcpEndpointAndSnippetsThatCarryAPlaceholderNeverASecret()
    {
        await using DashboardTestContext context = new();
        context.Services.AddSingleton(new DashboardOptions { MinPasswordLength = 12, McpEnabled = true });
        IdentityPages.SignIn(context, Role.Admin);
        context.Render<MudPopoverProvider>();
        var page = context.Render<ApiTokens>();
        await page.WaitForStateAsync(() => page.FindAll("[data-section=mcp]").Count == 1);

        var endpoint = new Uri(context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().BaseUri + "mcp").ToString();
        Assert.Equal(endpoint, page.Find("[data-field=mcp-url]").TextContent);
        var code = page.Find("[data-snippet=claude-code] pre").TextContent;
        Assert.Equal($"claude mcp add --transport http paduraksa {endpoint} --header \"Authorization: Bearer <token>\"", code);
        var desktop = page.Find("[data-snippet=claude-desktop] pre").TextContent;
        Assert.Contains(endpoint, desktop, StringComparison.Ordinal);
        Assert.Contains("\"PADURAKSA_TOKEN\": \"Bearer <token>\"", desktop, StringComparison.Ordinal);
        Assert.DoesNotContain("pdk_", page.Find("[data-section=mcp]").TextContent, StringComparison.Ordinal);

        var note = page.Find("[data-field=mcp-note]");
        Assert.Contains("can only read", note.TextContent, StringComparison.Ordinal);
        Assert.Contains("everything its scopes allow", note.TextContent, StringComparison.Ordinal);
        Assert.Contains(note.QuerySelectorAll("a").Select(a => a.GetAttribute("href")), href => href == DashboardRoutes.LocalCa);
    }

    [Fact]
    public async Task WhileMcpIsOffTheTokensPageSaysHowToTurnItOnInsteadOfShowingSnippets()
    {
        await using DashboardTestContext context = new();
        IdentityPages.SignIn(context, Role.Admin);
        context.Render<MudPopoverProvider>();
        var page = context.Render<ApiTokens>();
        await page.WaitForStateAsync(() => page.FindAll("[data-section=mcp-off]").Count == 1);
        Assert.Empty(page.FindAll("[data-section=mcp]"));
        Assert.Contains("Paduraksa__Mcp__Enabled=true", page.Find("[data-section=mcp-off]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANewToken_ShowsItsSecretOnce_InItsDrawer_AndForgetsItWhenTheDrawerCloses()
    {
        await using DashboardTestContext context = new();
        var (identity, _, _, _) = IdentityPages.SignIn(context, Role.Operator);
        context.Render<MudPopoverProvider>();
        var dialogs = context.Render<DrawerHost>();
        var page = context.Render<ApiTokens>();
        await page.WaitForStateAsync(() => page.Markup.Contains("No API tokens yet", StringComparison.Ordinal));

        // The drawer holds the page's click until it closes.
        var creating = page.Find("[data-action=add-token]").ClickAsync(new());
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
        var request = Assert.Single(identity.Issued);
        Assert.Equal("backup script", request.Name);
        Assert.All(request.Scopes, scope => Assert.True(Paduraksa.Dashboard.Identity.IdentityText.IsRead(scope)));
        Assert.NotNull(request.ExpiresAt);

        // Leaving the page asks first, and saying so keeps the secret where it is.
        var navigation = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var here = navigation.Uri;
        await dialogs.InvokeAsync(() => navigation.NavigateTo(Paduraksa.Dashboard.DashboardRoutes.Hosts));
        Assert.Contains("You haven't copied the token yet", dialogs.Find("[data-drawer=discard]").TextContent, StringComparison.Ordinal);
        Assert.Equal(here, navigation.Uri);
        await dialogs.Find("[data-drawer=keep]").ClickAsync(new());
        Assert.Single(dialogs.FindAll("[data-secret=token]"));

        var drawer = dialogs.FindComponent<Paduraksa.Dashboard.AccountArea.TokenDrawer>().Instance;
        await dialogs.Find("[data-action=done]").ClickAsync(new());
        await creating.WaitAsync(TimeSpan.FromSeconds(15), Xunit.TestContext.Current.CancellationToken);

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
        var (identity, _, _, me) = SignInOwner(context);
        var grace = FakeDashboardIdentity.User("grace", Role.Operator) with { DisplayName = "Grace Hopper" };
        identity.Users.Add(grace);
        identity.Users.Add(FakeDashboardIdentity.User("linus", Role.Viewer));
        identity.Tokens.Add(Token(me.Id, "mine"));
        identity.Tokens.Add(Token(grace.Id, "graces"));
        context.Render<MudPopoverProvider>();
        var page = context.Render<ApiTokens>();
        await page.WaitForStateAsync(() => page.FindAll("[data-token=mine]").Count == 1);

        await page.InvokeAsync(() => page.FindComponent<SegmentedControl<bool>>().Instance.ValueChanged.InvokeAsync(true));

        await page.WaitForStateAsync(() => page.FindAll("[data-token=graces]").Count == 1);
        Assert.Contains("Grace Hopper", page.Find("[data-token=graces]").Closest("[role=row]")!.TextContent, StringComparison.Ordinal);
        Assert.Single(identity.Calls, call => call == "list all tokens");
    }

    [Fact]
    public async Task EditingTheLastOwner_ShowsTheGuardsRefusal()
    {
        await using DashboardTestContext context = new();
        var (identity, _, _, me) = SignInOwner(context);
        identity.UpdateStatus = IdentityStatus.LastOwner;
        context.Render<MudPopoverProvider>();
        var dialogs = context.Render<DrawerHost>();
        var page = context.Render<Users>();
        await page.WaitForStateAsync(() => page.FindAll("[data-action=edit]").Count == 1);

        _ = page.Find("[data-action=edit]").ClickAsync(new());
        await dialogs.WaitForStateAsync(() => dialogs.FindAll("[data-role]").Count == 5);
        await dialogs.Find("[data-role=owner] input").ChangeAsync(new() { Value = false });
        await dialogs.Find("[data-role=admin] input").ChangeAsync(new() { Value = true });
        await dialogs.Find("form[data-drawer=user]").SubmitAsync();

        await dialogs.WaitForStateAsync(() => dialogs.FindAll("[data-refusal=last-owner]").Count == 1);
        Assert.Contains("at least one enabled Owner", dialogs.Find("[data-refusal=last-owner]").TextContent, StringComparison.Ordinal);
        var (id, sent) = Assert.Single(identity.Updates);
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
        var dialogs = context.Render<DrawerHost>();
        var page = context.Render<Users>();
        await page.WaitForStateAsync(() => page.FindAll("[data-action=edit]").Count == 1);
        var editing = page.Find("[data-action=edit]").ClickAsync(new());
        await dialogs.WaitForStateAsync(() => dialogs.FindAll("input[data-field=display-name]").Count == 1);
        var name = dialogs.Find("input[data-field=display-name]").GetAttribute("value")!;

        await dialogs.Find("input[data-field=display-name]").InputAsync(new() { Value = "Someone else" });
        await dialogs.Find("input[data-field=display-name]").InputAsync(new() { Value = name });
        await dialogs.InvokeAsync(dialogs.Instance.EscapeAsync);

        Assert.Empty(dialogs.FindAll("[data-drawer=discard]"));
        await editing.WaitAsync(TimeSpan.FromSeconds(15), Xunit.TestContext.Current.CancellationToken);
        Assert.Empty(dialogs.FindAll(".pdk-sheet"));
    }

    [Fact]
    public async Task AddingAUser_ChecksTheFormFirst_AndSendsTheRoles()
    {
        await using DashboardTestContext context = new();
        var (identity, _, _, _) = SignInOwner(context);
        context.Render<MudPopoverProvider>();
        var dialogs = context.Render<DrawerHost>();
        var page = context.Render<Users>();
        await page.WaitForStateAsync(() => page.FindAll("[data-action=add-user]").Count == 1);
        Assert.Contains("Reads the config and certificates and runs the simulator", page.Find("[data-section=roles]").TextContent, StringComparison.Ordinal);

        var adding = page.Find("[data-action=add-user]").ClickAsync(new());
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

        var created = Assert.Single(identity.Created);
        Assert.Equal("viewer", created.Username);
        Assert.Equal([Role.Viewer], created.Roles);
        await adding.WaitAsync(TimeSpan.FromSeconds(15), Xunit.TestContext.Current.CancellationToken);
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
        var page = context.Render<Users>();

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
        var (_, _, config, _) = IdentityPages.SignIn(context, role);
        config.Document = new ConfigDocument(Settings: new GlobalSettings(Version: 4));
        context.Render<MudPopoverProvider>();
        context.Render<Paduraksa.Dashboard.Drawers.DrawerHost>();
        var page = context.Render<GlobalDefaults>();
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
        var command = Assert.IsType<UpdateSettingsCommand>(config.Executed[0]);
        Assert.True(command.DryRun);
        Assert.Equal(4, command.Settings.Version);
        Assert.Equal(TrustedProxyMode.CloudflareProxy, command.Settings.TrustedProxies.Mode);
        Assert.Empty(command.Settings.TrustedProxies.Proxies);
    }

    [Fact]
    public async Task TheClientAddressCardShowsTheEdgeRangeListWhereTheProxyModeNeedsIt()
    {
        await using DashboardTestContext context = new();
        var (_, _, config, _) = IdentityPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(Settings: new GlobalSettings(Version: 4));
        var now = TimeProvider.System.GetUtcNow();
        config.CloudflareRanges = new CloudflareRangeStatus(CloudflareRangeSource.Fetched, now.AddDays(-3), 22, 0, null);
        context.Render<MudPopoverProvider>();
        var page = context.Render<GlobalDefaults>();
        await page.WaitForStateAsync(() => page.FindAll("[data-form=defaults]").Count == 1);

        // Only the Cloudflare proxy mode trusts the list.
        Assert.Empty(page.FindAll("[data-field=cloudflare-ranges]"));
        await page.Find("[data-mode=cloudflareproxy]").ClickAsync(new());
        var line = page.Find("[data-field=cloudflare-ranges]");
        Assert.Equal("ok", line.GetAttribute("data-state"));
        Assert.Equal("Cloudflare ranges: updated 3 days ago (22 ranges)", line.TextContent.Trim());

        // The warning replaces the line, in the words the compiler gives.
        config.CloudflareRanges = config.CloudflareRanges with { Warning = "Cloudflare's edge ranges are the stored list, 40 days old." };
        await page.Find("[data-mode=cloudflaretunnel]").ClickAsync(new());
        Assert.Empty(page.FindAll("[data-field=cloudflare-ranges]"));
        await page.Find("[data-mode=cloudflareproxy]").ClickAsync(new());
        var warning = page.Find("[data-field=cloudflare-ranges]");
        Assert.Equal("warning", warning.GetAttribute("data-state"));
        Assert.Contains("40 days old", warning.TextContent, StringComparison.Ordinal);

        // The host settings on this page say what they need while the mode is something else.
        await page.Find("[data-mode=off]").ClickAsync(new());
        Assert.Single(page.FindAll("[data-field=cloudflare-needs-mode]"));
        await page.Find("[data-mode=cloudflareproxy]").ClickAsync(new());
        Assert.Empty(page.FindAll("[data-field=cloudflare-needs-mode]"));
    }

    [Fact]
    public async Task AProfileInUse_CannotBeDeleted_AndSaysWhichHosts()
    {
        await using DashboardTestContext context = new();
        var (_, _, config, _) = IdentityPages.SignIn(context, Role.Operator);
        ProfileConfig lan = new(ProfileId.New(), "LAN only", 1, new HostSettings { ForceHttps = true });
        HostConfig host = new(HostId.New(), ["nas.home.test"], Version: 1, ProfileId: lan.Id, Kind: HostKind.Respond, Respond: new RespondConfig(200, "ok"));
        config.Document = new ConfigDocument(Profiles: [lan], Hosts: [host]);
        var page = context.Render<Profiles>();
        await page.WaitForStateAsync(() => page.FindAll("[data-profile='LAN only']").Count == 1);
        Assert.Contains("Redirects to HTTPS", page.Find("[data-profile='LAN only']").TextContent, StringComparison.Ordinal);
        Assert.Contains("nas.home.test", page.Find("[data-profile='LAN only']").TextContent, StringComparison.Ordinal);

        await page.Find("[data-action=delete]").ClickAsync(new());

        Assert.Contains("still used by nas.home.test", page.Find("[data-banner=in-use]").TextContent, StringComparison.Ordinal);
        Assert.Empty(config.Executed);
    }

    private static (FakeDashboardIdentity, Paduraksa.Dashboard.Audit.IDashboardAudit, FakeDashboardConfig, DashboardPrincipalRef) SignInOwner(DashboardTestContext context)
    {
        var (identity, audit, config, user) = IdentityPages.SignIn(context, Role.Owner);
        return (identity, audit, config, new DashboardPrincipalRef(user.Session.Session.UserId));
    }

    private static ApiTokenSummary Token(UserId owner, string name) =>
        new(ApiTokenId.New(), owner, name, [Permission.ConfigRead], null, DateTimeOffset.UtcNow.AddDays(-1), null, false);

    private static object? Field(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);

    private sealed record DashboardPrincipalRef(UserId Id);
}
