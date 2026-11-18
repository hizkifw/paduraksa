using Bunit;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using MudBlazor;

using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.AccountArea;
using Paduraksa.Dashboard.Components;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>An API token's drawer, opened from the tokens list or its address: its facts, changing it, switching it off and on, revoking it, and what is read-only.</summary>
public sealed class TokenDetailsTests
{
    private static ApiTokenSummary Token(UserId owner, string name, params Permission[] scopes) =>
        new(ApiTokenId.New(), owner, name, scopes.Length == 0 ? [Permission.ConfigRead] : scopes, null, DateTimeOffset.UtcNow.AddDays(-1), null, false, false);

    private static async Task<(DashboardTestContext Context, FakeDashboardIdentity Identity, IRenderedComponent<DrawerHost> Drawers, IRenderedComponent<ApiTokens> Page, ApiTokenSummary Token)> OpenAsync(
        Role role, Func<UserId, ApiTokenSummary>? make = null, Action<FakeDashboardIdentity>? arrange = null)
    {
        DashboardTestContext context = new();
        var (identity, _, _, user) = IdentityPages.SignIn(context, role);
        var token = (make ?? (owner => Token(owner, "backup script")))(user.Session.Session.UserId);
        identity.Tokens.Add(token);
        arrange?.Invoke(identity);
        context.Render<MudPopoverProvider>();
        context.Render<MudDialogProvider>();
        var drawers = context.Render<DrawerHost>();
        var page = context.Render<ApiTokens>(parameters => parameters.Add(p => p.Key, token.Id.ToString()));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=token-details]").Count + drawers.FindAll("[data-drawer=not-found]").Count == 1);
        return (context, identity, drawers, page, token);
    }

    [Fact]
    public void TheRouteHelperIsTheListWithTheTokensIdAfterIt()
    {
        var id = ApiTokenId.New();

        Assert.Equal($"/settings/tokens/{id}", DashboardRoutes.Token(id));
    }

    [Fact]
    public async Task ATokensAddressOpensItsDrawerWithItsFactsAndNeverASecret()
    {
        var (context, _, drawers, _, token) = await OpenAsync(Role.Operator, owner => Token(owner, "backup script", Permission.ConfigRead, Permission.MetricsRead) with
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            LastUsedAt = DateTimeOffset.UtcNow.AddHours(-2),
        });
        await using var scope = context;

        Assert.Equal("backup script", drawers.Find(".pdk-drawer-title").TextContent);
        Assert.Equal("Active", drawers.Find("[data-chip=status]").TextContent.Trim());
        Assert.Equal("You", drawers.Find("[data-fact=owner]").TextContent.Trim());
        Assert.Equal(token.Id.ToString(), drawers.Find("[data-fact=id] .pdk-copy-text").TextContent.Trim());
        Assert.DoesNotContain("Never", drawers.Find("[data-fact=expires]").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Never", drawers.Find("[data-fact=last-used]").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeDashboardIdentity.TokenSecret, drawers.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("pdk_", drawers.Markup, StringComparison.Ordinal);
        string[] selected = [.. drawers.FindAll(".pdk-scope-option.pdk-selected").Select(e => e.GetAttribute("data-scope")!)];
        Assert.Equal(["config:read", "metrics:read"], selected);
    }

    [Theory]
    [InlineData("not-an-id")]
    [InlineData("usr_0J9zb3xQk1Lr8m2V7aYc4D")]
    public async Task AnAddressThatIsNotAnIdOfAListedToken_IsNotFound(string key)
    {
        await using DashboardTestContext context = new();
        IdentityPages.SignIn(context, Role.Operator);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();

        context.Render<ApiTokens>(parameters => parameters.Add(p => p.Key, key));

        await drawers.WaitForStateAsync(() => drawers.Markup.Contains("There is no such token", StringComparison.Ordinal));
        Assert.Equal("Token not found", drawers.Find(".pdk-drawer-title").TextContent);
    }

    [Fact]
    public async Task SomeoneElsesTokenIsNotFoundWithoutTokensAll()
    {
        await using DashboardTestContext context = new();
        var (identity, _, _, _) = IdentityPages.SignIn(context, Role.Viewer);
        var grace = FakeDashboardIdentity.User("grace", Role.Viewer);
        var theirs = Token(grace.Id, "graces script");
        identity.Users.Add(grace);
        identity.Tokens.Add(theirs);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();

        context.Render<ApiTokens>(parameters => parameters.Add(p => p.Key, theirs.Id.ToString()));

        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=not-found]").Count == 1);
    }

    /// <summary>An Owner opening, by its address, a Viewer's token, which is in the list of everyone's and not in their own.</summary>
    private static async Task<(DashboardTestContext Context, FakeDashboardIdentity Identity, IRenderedComponent<DrawerHost> Drawers)> OpenOthersAsync()
    {
        DashboardTestContext context = new();
        var (identity, _, _, _) = IdentityPages.SignIn(context, Role.Owner);
        var grace = FakeDashboardIdentity.User("grace", Role.Viewer) with { DisplayName = "Grace Hopper" };
        var theirs = Token(grace.Id, "graces script");
        identity.Users.Add(grace);
        identity.Tokens.Add(theirs);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<ApiTokens>(parameters => parameters.Add(p => p.Key, theirs.Id.ToString()));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=token-details]").Count == 1);
        return (context, identity, drawers);
    }

    [Fact]
    public async Task ATokenInTheListOfEveryonesOpensForAnOwnerWithItsOwnersName()
    {
        var (context, _, drawers) = await OpenOthersAsync();
        await using var scope = context;

        Assert.Equal("graces script", drawers.Find(".pdk-drawer-title").TextContent);
        Assert.Equal("Grace Hopper", drawers.Find("[data-fact=owner]").TextContent.Trim());
        Assert.Single(drawers.FindAll("[data-action=revoke]"));
    }

    [Fact]
    public async Task TheOwnerOfATokenIsALinkToTheirDrawerOnTheUsersPage()
    {
        var (context, identity, drawers) = await OpenOthersAsync();
        await using var scope = context;
        var grace = identity.Users.Single(u => u.Username == "grace");

        Assert.Equal(Paduraksa.Dashboard.SettingsRoutes.User(grace.Id), drawers.Find("[data-fact=owner] a[data-link=owner]").GetAttribute("href"));
    }

    [Fact]
    public async Task TheScopesOfferedAreWhatTheTokensOwnerHoldsNotWhatTheEditorHolds()
    {
        var (context, identity, drawers) = await OpenOthersAsync();
        await using var scope = context;

        string[] offered = [.. drawers.FindAll("[data-scope]").Select(e => e.GetAttribute("data-scope")!)];

        // A Viewer reads and simulates; the Owner who is editing holds config:write and users:write as well, and can't hand them over.
        Assert.Contains("config:read", offered);
        Assert.DoesNotContain("config:write", offered);
        Assert.DoesNotContain("users:write", offered);
        Assert.Empty(identity.TokenUpdates);
    }

    [Fact]
    public async Task ARowOpensItsAddress()
    {
        await using DashboardTestContext context = new();
        var (identity, _, _, user) = IdentityPages.SignIn(context, Role.Operator);
        var token = Token(user.Session.Session.UserId, "backup script");
        identity.Tokens.Add(token);
        context.Render<MudPopoverProvider>();
        var page = context.Render<ApiTokens>();
        await page.WaitForStateAsync(() => page.FindAll("[data-token]").Count == 1);

        await page.Find("[role=row].pdk-dt-link").ClickAsync(new());

        Assert.EndsWith(DashboardRoutes.Token(token.Id), context.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
        Assert.Equal(DashboardRoutes.Token(token.Id), page.Find("a.pdk-row-link").GetAttribute("href"));
        Assert.Empty(page.FindAll("[data-action=revoke]"));
    }

    [Fact]
    public async Task ARenamedAndRescopedToken_IsSentAsOneChangeOfOnlyWhatChanged_AndTheDrawerClosesToTheList()
    {
        var (context, identity, drawers, page, token) = await OpenAsync(Role.Operator);
        await using var scope = context;

        await drawers.Find("input[data-field=token-name]").InputAsync(new() { Value = "  deploy  " });
        await drawers.Find("[data-scope='config:write'] input").ChangeAsync(new() { Value = true });
        await drawers.Find("form[data-drawer=token-details]").SubmitAsync();

        await drawers.WaitForStateAsync(() => drawers.FindAll(".pdk-sheet").Count == 0);
        var (id, request) = Assert.Single(identity.TokenUpdates);
        Assert.Equal(token.Id, id);
        Assert.Equal("deploy", request.Name);
        Assert.Equal([Permission.ConfigRead, Permission.ConfigWrite], request.Scopes);
        Assert.Null(request.Disabled);
        Assert.EndsWith(SettingsRoutes.Tokens, context.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
        await page.WaitForStateAsync(() => page.FindAll("[data-token=deploy]").Count == 1);
    }

    [Fact]
    public async Task NothingChangedSendsNothing()
    {
        var (context, identity, drawers, _, _) = await OpenAsync(Role.Operator);
        await using var scope = context;

        await drawers.Find("form[data-drawer=token-details]").SubmitAsync();
        await drawers.WaitForStateAsync(() => drawers.FindAll(".pdk-sheet").Count == 0);

        Assert.Empty(identity.TokenUpdates);
    }

    [Fact]
    public async Task ARenameAlone_LeavesTheScopesOutOfTheRequest()
    {
        var (context, identity, drawers, _, _) = await OpenAsync(Role.Operator);
        await using var scope = context;

        await drawers.Find("input[data-field=token-name]").InputAsync(new() { Value = "renamed" });
        await drawers.Find("form[data-drawer=token-details]").SubmitAsync();

        await drawers.WaitForStateAsync(() => identity.TokenUpdates.Count == 1);
        var (_, request) = Assert.Single(identity.TokenUpdates);
        Assert.Equal("renamed", request.Name);
        Assert.Null(request.Scopes);
    }

    [Fact]
    public async Task AnEmptyNameOrNoScopeIsRefusedInTheDrawerAndSendsNothing()
    {
        var (context, identity, drawers, _, _) = await OpenAsync(Role.Operator);
        await using var scope = context;

        await drawers.Find("input[data-field=token-name]").InputAsync(new() { Value = " " });
        await drawers.Find("[data-scope='config:read'] input").ChangeAsync(new() { Value = false });
        await drawers.Find("form[data-drawer=token-details]").SubmitAsync();

        Assert.Contains("A token needs a name.", drawers.Markup, StringComparison.Ordinal);
        Assert.Contains("Pick at least one scope.", drawers.Markup, StringComparison.Ordinal);
        Assert.Empty(identity.TokenUpdates);
    }

    [Fact]
    public async Task DisablingAndEnablingAreDrawerActionsThatKeepItOpen_AndTheListShowsThePill()
    {
        var (context, identity, drawers, page, token) = await OpenAsync(Role.Operator);
        await using var scope = context;
        Assert.Empty(drawers.FindAll("[data-action=enable]"));

        await drawers.Find("[data-action=disable]").ClickAsync(new());

        Assert.Equal(true, Assert.Single(identity.TokenUpdates).Request.Disabled);
        Assert.Equal("Disabled", drawers.Find("[data-chip=status]").TextContent.Trim());
        Assert.Empty(drawers.FindAll("[data-action=disable]"));
        await page.WaitForStateAsync(() => page.Find($"[data-token='{token.Name}']").Closest("[role=row]")!.QuerySelector("[data-chip=status]")!.TextContent.Trim() == "Disabled");
        Assert.Contains("pdk-dt-off", page.Find($"[data-token='{token.Name}']").Closest("[role=row]")!.ClassName, StringComparison.Ordinal);

        await drawers.Find("[data-action=enable]").ClickAsync(new());

        Assert.Equal(false, identity.TokenUpdates[1].Request.Disabled);
        Assert.Equal("Active", drawers.Find("[data-chip=status]").TextContent.Trim());
        Assert.Single(drawers.FindAll("[data-drawer=token-details]"));
    }

    [Fact]
    public async Task ARefusedChangeIsShownInTheDrawerWhichStaysOpen()
    {
        var (context, identity, drawers, _, _) = await OpenAsync(Role.Operator, arrange: identity =>
        {
            identity.TokenUpdateStatus = IdentityStatus.Conflict;
            identity.TokenUpdateProblem = "A revoked token can't be changed.";
        });
        await using var scope = context;

        await drawers.Find("[data-action=disable]").ClickAsync(new());

        Assert.Equal("A revoked token can't be changed.", drawers.Find("[data-form=error]").TextContent.Trim());
        Assert.Equal("Active", drawers.Find("[data-chip=status]").TextContent.Trim());
        Assert.Single(identity.TokenUpdates);
    }

    [Fact]
    public async Task RevokingAsksFirst_ThenRevokesAndClosesTheDrawer()
    {
        var (context, identity, drawers, page, token) = await OpenAsync(Role.Operator);
        await using var scope = context;
        var dialogs = context.Render<MudDialogProvider>();

        // The click waits for the dialog's answer, so it isn't awaited here.
        var asking = drawers.Find("[data-action=revoke]").ClickAsync(new());
        await dialogs.WaitForStateAsync(() => dialogs.FindAll(".pdk-confirm").Count == 1);
        Assert.Contains("This can't be undone", dialogs.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(identity.Calls, call => call.StartsWith("revoke", StringComparison.Ordinal));
        await dialogs.Find(".pdk-confirm").ClickAsync(new());
        await asking;

        await drawers.WaitForStateAsync(() => drawers.FindAll(".pdk-sheet").Count == 0);
        Assert.Contains($"revoke {token.Id}", identity.Calls);
        await page.WaitForStateAsync(() => page.FindAll("[data-token]").Count == 0);
    }

    [Fact]
    public async Task DecliningTheRevokeConfirmationRevokesNothing()
    {
        var (context, identity, drawers, _, _) = await OpenAsync(Role.Operator);
        await using var scope = context;
        var dialogs = context.Render<MudDialogProvider>();

        var asking = drawers.Find("[data-action=revoke]").ClickAsync(new());
        await dialogs.WaitForStateAsync(() => dialogs.FindAll(".pdk-confirm").Count == 1);
        await dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").ClickAsync(new());
        await asking;

        Assert.DoesNotContain(identity.Calls, call => call.StartsWith("revoke", StringComparison.Ordinal));
        Assert.Single(drawers.FindAll("[data-drawer=token-details]"));
    }

    [Fact]
    public async Task ARevokedTokenShowsItsFactsAsValuesWithNoFieldsAndNoActions()
    {
        var (context, _, drawers, _, _) = await OpenAsync(Role.Owner, owner => Token(owner, "old script", Permission.ConfigRead, Permission.MetricsRead) with { Revoked = true });
        await using var scope = context;

        Assert.Equal("Revoked", drawers.Find("[data-chip=status]").TextContent.Trim());
        Assert.Equal("old script", drawers.Find("[data-readonly=token-name] [data-part=value]").TextContent.Trim());
        Assert.Equal(["config:read", "metrics:read"], drawers.FindAll("[data-fact=scopes] .pdk-tag").Select(e => e.TextContent.Trim()));
        Assert.Empty(drawers.FindAll("input[data-field=token-name]"));
        Assert.Empty(drawers.FindAll("[data-scope]"));
        foreach (var action in new[] { "revoke", "disable", "enable", "save-token" })
        {
            Assert.Empty(drawers.FindAll($"[data-action={action}]"));
        }

        Assert.Single(drawers.FindAll("[data-fact=id]"));
    }

    [Fact]
    public async Task ScopesTheTokenCannotUseAreShownDisabledAndKeptOutOfARename()
    {
        // A Viewer's token that still carries config:write, which its owner has lost, and a scope that tokens may not carry.
        var (context, identity, drawers, _, _) = await OpenAsync(Role.Viewer, owner => Token(owner, "stale", Permission.ConfigRead, Permission.ConfigWrite, Permission.TokensOwn));
        await using var scope = context;

        Assert.Equal(["config:write", "tokens:own"], drawers.FindAll("[data-dropped]").Select(e => e.GetAttribute("data-dropped")));
        Assert.Contains("Saving changed scopes removes them", drawers.Find("[data-notice=dropped-scopes]").TextContent, StringComparison.Ordinal);
        Assert.Equal(["config:read"], drawers.FindAll(".pdk-scope-option.pdk-selected").Select(e => e.GetAttribute("data-scope")));

        await drawers.Find("input[data-field=token-name]").InputAsync(new() { Value = "renamed" });
        await drawers.Find("form[data-drawer=token-details]").SubmitAsync();

        await drawers.WaitForStateAsync(() => identity.TokenUpdates.Count == 1);
        Assert.Null(identity.TokenUpdates[0].Request.Scopes);
        Assert.Equal([Permission.ConfigRead, Permission.ConfigWrite, Permission.TokensOwn], identity.Tokens[0].Scopes);
    }

    [Fact]
    public async Task ChangingTheScopesOfATokenWithDroppedOnesSendsOnlyWhatIsSelected()
    {
        var (context, identity, drawers, _, _) = await OpenAsync(Role.Viewer, owner => Token(owner, "stale", Permission.ConfigRead, Permission.ConfigWrite));
        await using var scope = context;

        await drawers.Find("[data-scope='metrics:read'] input").ChangeAsync(new() { Value = true });
        await drawers.Find("form[data-drawer=token-details]").SubmitAsync();

        await drawers.WaitForStateAsync(() => identity.TokenUpdates.Count == 1);
        Assert.Equal([Permission.ConfigRead, Permission.MetricsRead], identity.TokenUpdates[0].Request.Scopes);
    }

    [Fact]
    public async Task ADisabledTokenHasItsOwnToneInTheDrawerAndTheList()
    {
        var (context, _, drawers, page, _) = await OpenAsync(Role.Operator, owner => Token(owner, "paused") with { Disabled = true });
        await using var scope = context;

        Assert.Contains("pdk-tone-warning", drawers.Find("[data-chip=status]").ClassName, StringComparison.Ordinal);
        await page.WaitForStateAsync(() => page.FindAll("[data-token=paused]").Count == 1);
        Assert.Contains("pdk-tone-warning", page.Find("[data-token=paused]").Closest("[role=row]")!.QuerySelector("[data-chip=status]")!.ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATokenTheCallerMayNotChangeIsReadOnly()
    {
        await using DashboardTestContext context = new();
        IdentityPages.SignIn(context, Role.Viewer);
        context.Render<MudPopoverProvider>();
        var token = Token(UserId.New(), "someone's script");

        var drawer = context.Render<TokenDetailsDrawer>(parameters => parameters
            .Add(p => p.Token, token)
            .Add(p => p.Owner, "Grace Hopper")
            .Add(p => p.OwnerHolds, new HashSet<Permission> { Permission.ConfigRead })
            .Add(p => p.CanEdit, false));

        Assert.Equal("Grace Hopper", drawer.Find("[data-fact=owner]").TextContent.Trim());
        Assert.Empty(drawer.FindAll("input[data-field=token-name]"));
        Assert.Empty(drawer.FindAll("[data-action=revoke]"));
        Assert.Empty(drawer.FindAll("[data-action=save-token]"));
        Assert.Single(drawer.FindAll("[data-fact=scopes]"));
    }

    [Theory]
    [InlineData(false, false, "Active")]
    [InlineData(true, false, "Disabled")]
    [InlineData(false, true, "Revoked")]
    [InlineData(true, true, "Revoked")]
    public void AStateIsRevokedThenDisabledThenExpiredThenActive(bool disabled, bool revoked, string expected)
    {
        var token = Token(UserId.New(), "t") with { Disabled = disabled, Revoked = revoked };

        Assert.Equal(expected, Paduraksa.Dashboard.Identity.IdentityText.State(token, DateTimeOffset.UtcNow).Label);
        Assert.Equal("Expired", Paduraksa.Dashboard.Identity.IdentityText.State(Token(UserId.New(), "t") with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) }, DateTimeOffset.UtcNow).Label);
        Assert.Equal("Disabled", Paduraksa.Dashboard.Identity.IdentityText.State(token with { Disabled = true, Revoked = false, ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) }, DateTimeOffset.UtcNow).Label);
    }
}
