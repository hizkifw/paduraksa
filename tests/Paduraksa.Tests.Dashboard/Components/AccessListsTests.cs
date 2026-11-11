using Bunit;

using MudBlazor;

using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The access lists: a page that says what each list lets in and who applies it, an editor with its addresses and users, and
/// create, edit and delete through the same review the other objects use. A password is typed or generated, sent once, and
/// kept as its stored hash when the box stays empty. A list in use offers no delete, because the validator would refuse it.
/// </summary>
public sealed class AccessListsTests
{
    private static readonly PasswordCredentialId AliceCredential = PasswordCredentialId.New();
    private static readonly PasswordCredentialId BobCredential = PasswordCredentialId.New();

    private static readonly AccessListConfig Office = new(
        AccessListId.New(),
        "Office",
        Ip: new AccessListIp(["10.0.0.0/8", "192.168.1.7"], ["10.9.0.0/16"]),
        BasicAuth: new AccessListBasicAuth("Staff area", [new AccessListUserConfig("alice", AliceCredential), new AccessListUserConfig("bob", BobCredential)]),
        Satisfy: AccessListSatisfy.Any);

    private static readonly AccessListConfig Guests = new(AccessListId.New(), "Guests", Ip: new AccessListIp(["203.0.113.0/24"]));

    [Fact]
    public async Task ThePage_ListsEachListWithItsRulesAndTheHostsAndRoutesThatUseIt()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        HostConfig host = new(
            HostId.New(), ["nas.home.test"], Routes: [new RouteConfig(RouteId.New(), UpstreamId.New(), Path: "/admin", Settings: new RouteSettings { AccessList = Use(Guests.Id) })],
            Settings: new HostSettings { AccessList = Use(Office.Id) });
        config.Document = new ConfigDocument(AccessLists: [Office, Guests], Hosts: [host]);

        var page = context.Render<AccessLists>();

        await page.WaitForStateAsync(() => page.FindAll("[data-access-list]").Count == 2);
        var office = page.Find("[data-access-list=Office]");
        Assert.Contains("2 allowed, 1 denied, 2 users, address or password", office.TextContent, StringComparison.Ordinal);
        Assert.Contains("1 host", office.TextContent, StringComparison.Ordinal);
        Assert.Contains("nas.home.test", office.TextContent, StringComparison.Ordinal);
        var guests = page.Find("[data-access-list=Guests]");
        Assert.Contains("1 allowed", guests.TextContent, StringComparison.Ordinal);
        Assert.Contains("1 route", guests.TextContent, StringComparison.Ordinal);
        Assert.Contains("/admin", guests.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AListInUse_OffersNoDelete_AndAnUnusedOneDoes()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        HostConfig host = new(HostId.New(), ["nas.home.test"], Settings: new HostSettings { AccessList = Use(Office.Id) });
        config.Document = new ConfigDocument(AccessLists: [Office, Guests], Hosts: [host]);

        var page = context.Render<AccessLists>();

        await page.WaitForStateAsync(() => page.FindAll("[data-access-list]").Count == 2);
        Assert.Empty(page.FindAll("[data-access-list=Office] [data-action=delete]"));
        Assert.Single(page.FindAll("[data-access-list=Guests] [data-action=delete]"));
    }

    [Fact]
    public async Task APageWithoutLists_SaysWhatOneIsFor()
    {
        await using DashboardTestContext context = new();
        ConfigPages.SignIn(context, Role.Operator).Document = new ConfigDocument();

        var page = context.Render<AccessLists>();

        await page.WaitForStateAsync(() => page.FindAll(".pdk-empty").Count == 1);
        Assert.Contains("No access lists yet", page.Find(".pdk-empty").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreatingAList_SendsItsAddressesAndTypedPasswordsThroughReview()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument();
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<AccessLists>(parameters => parameters.Add(p => p.Key, DashboardRoutes.NewSegment));

        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=access-list] input[data-field=name]").Count == 1);
        await TypeAsync(drawers, "[data-drawer=access-list] input[data-field=name]", "Office");
        await TypeAsync(drawers, "[data-drawer=access-list] textarea[data-field=allowed]", "10.0.0.0/8\n192.168.1.7");
        await TypeAsync(drawers, "[data-drawer=access-list] textarea[data-field=denied]", "10.9.0.0/16");
        await drawers.Find("[data-action=add-user]").ClickAsync(new());
        await TypeAsync(drawers, "[data-drawer=access-list] input[data-field=username-0]", "alice");
        await TypeAsync(drawers, "[data-drawer=access-list] input[data-field=password-0]", "correct horse");
        await drawers.InvokeAsync(() => drawers.FindComponents<MudSelect<AccessListSatisfy>>()
            .Single(select => select.Instance.Label == "A visitor must pass")
            .Instance.ValueChanged.InvokeAsync(AccessListSatisfy.Any));

        var reviewing = drawers.Find("form[data-drawer=access-list]").SubmitAsync();
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=apply]").Count == 1);
        var dry = Assert.IsType<UpsertAccessListCommand>(config.Executed[0]);
        Assert.True(dry.DryRun);
        var list = dry.AccessList;
        Assert.Equal(("Office", 0L, AccessListSatisfy.Any), (list.Name, list.Version, list.Satisfy));
        Assert.Equal(["10.0.0.0/8", "192.168.1.7"], list.Ip!.Allow);
        Assert.Equal(["10.9.0.0/16"], list.Ip.Deny);
        var user = Assert.Single(list.BasicAuth!.Users);
        Assert.Equal(("alice", null, "correct horse"), (user.Username, user.CredentialId, user.Password));

        await drawers.Find("[data-review=apply]").ClickAsync(new());
        await reviewing.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
        Assert.False(Assert.IsType<UpsertAccessListCommand>(config.Executed[^1]).DryRun);
    }

    [Fact]
    public async Task EditingAList_KeepsEveryStoredCredentialAndSendsANewPasswordOnlyWhereOneWasTyped()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        var stored = Office with { Version = 4 };
        config.Document = new ConfigDocument(AccessLists: [stored]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<AccessLists>(parameters => parameters.Add(p => p.Key, stored.Id.ToString()));

        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=access-list] input[data-field=username-1]").Count == 1);
        Assert.Equal("alice", drawers.Find("[data-drawer=access-list] input[data-field=username-0]").GetAttribute("value"));
        await TypeAsync(drawers, "[data-drawer=access-list] input[data-field=name]", "Head office");
        await TypeAsync(drawers, "[data-drawer=access-list] input[data-field=password-1]", "a new password");
        _ = drawers.Find("form[data-drawer=access-list]").SubmitAsync();
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=apply]").Count == 1);

        var edit = Assert.IsType<UpsertAccessListCommand>(config.Executed[0]).AccessList;
        Assert.Equal((stored.Id, 4L, "Head office"), (edit.Id, edit.Version, edit.Name));
        var users = edit.BasicAuth!.Users;
        Assert.Equal(("alice", AliceCredential, null), (users[0].Username, users[0].CredentialId, users[0].Password));
        Assert.Equal(("bob", BobCredential, "a new password"), (users[1].Username, users[1].CredentialId, users[1].Password));
        Assert.Equal("Staff area", edit.BasicAuth.Realm);
    }

    [Fact]
    public async Task Generate_FillsAPasswordAndShowsItOnceWithAWarning()
    {
        await using DashboardTestContext context = new();
        ConfigPages.SignIn(context, Role.Operator).Document = new ConfigDocument(AccessLists: [Office]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<AccessLists>(parameters => parameters.Add(p => p.Key, Office.Id.ToString()));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=access-list] [data-action=generate-0]").Count == 1);
        Assert.Equal("password", drawers.Find("[data-drawer=access-list] input[data-field=password-0]").GetAttribute("type"));
        Assert.Equal(string.Empty, drawers.Find("[data-drawer=access-list] input[data-field=password-0]").GetAttribute("value") ?? string.Empty);

        await drawers.Find("[data-drawer=access-list] [data-action=generate-0]").ClickAsync(new());

        var box = drawers.Find("[data-drawer=access-list] input[data-field=password-0]");
        Assert.Equal("text", box.GetAttribute("type"));
        var generated = box.GetAttribute("value");
        Assert.Equal(20, generated!.Length);
        Assert.Matches("^[A-Za-z0-9]+$", generated);
        Assert.Contains("Copy it now", drawers.Find("[data-drawer=access-list]").TextContent, StringComparison.Ordinal);

        // Typing over it is an ordinary edit again: masked.
        await TypeAsync(drawers, "[data-drawer=access-list] input[data-field=password-0]", "mine");
        Assert.Equal("password", drawers.Find("[data-drawer=access-list] input[data-field=password-0]").GetAttribute("type"));
        Assert.DoesNotContain("Copy it now", drawers.Find("[data-drawer=access-list]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUserTheVaultHoldsNoPasswordFor_SaysSoAndAsksForOneInsteadOfOfferingToKeepIt()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(AccessLists: [Office]);
        config.LiveWarnings = [new ConfigWarning("/accessLists/0/basicAuth/users/1", "accessList.user.noPassword", "the user has no password in the vault, so they can't sign in until one is set")];
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<AccessLists>(parameters => parameters.Add(p => p.Key, Office.Id.ToString()));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=access-list] input[data-field=password-1]").Count == 1);

        var alice = drawers.Find("[data-drawer=access-list] input[data-field=password-0]");
        var bob = drawers.Find("[data-drawer=access-list] input[data-field=password-1]");

        Assert.Equal("Unchanged", alice.GetAttribute("placeholder"));
        Assert.True(string.IsNullOrEmpty(bob.GetAttribute("placeholder")));
        Assert.Contains("No password is set, so this user can't sign in.", drawers.Find("[data-drawer=access-list]").TextContent, StringComparison.Ordinal);
        Assert.Single(drawers.FindAll("[data-drawer=access-list] .mud-input-helper-text"), e => e.TextContent.Contains("can't sign in", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TwoGeneratedPasswordsDiffer()
    {
        await using DashboardTestContext context = new();
        ConfigPages.SignIn(context, Role.Operator).Document = new ConfigDocument();
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<AccessLists>(parameters => parameters.Add(p => p.Key, DashboardRoutes.NewSegment));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-action=add-user]").Count == 1);
        await drawers.Find("[data-action=add-user]").ClickAsync(new());
        await drawers.Find("[data-action=add-user]").ClickAsync(new());

        await drawers.Find("[data-action=generate-0]").ClickAsync(new());
        await drawers.Find("[data-action=generate-1]").ClickAsync(new());

        Assert.NotEqual(
            drawers.Find("input[data-field=password-0]").GetAttribute("value"),
            drawers.Find("input[data-field=password-1]").GetAttribute("value"));
    }

    [Fact]
    public async Task RemovingAUser_DropsItsRow_AndTheSatisfyChoiceGoesWithTheLastUser()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(AccessLists: [Office with { Version = 2 }]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<AccessLists>(parameters => parameters.Add(p => p.Key, Office.Id.ToString()));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-action=remove-user-1]").Count == 1);
        Assert.Single(drawers.FindAll("[data-field=satisfy]"));

        await drawers.Find("[data-action=remove-user-1]").ClickAsync(new());
        await drawers.Find("[data-action=remove-user-0]").ClickAsync(new());

        Assert.Empty(drawers.FindAll("[data-user]"));
        Assert.Empty(drawers.FindAll("[data-field=satisfy]"));
        Assert.Contains("No users: only the addresses apply.", drawers.Find("[data-section=no-users]").TextContent, StringComparison.Ordinal);
        _ = drawers.Find("form[data-drawer=access-list]").SubmitAsync();
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=apply]").Count == 1);
        var sent = Assert.IsType<UpsertAccessListCommand>(config.Executed[0]).AccessList.BasicAuth!;
        Assert.Empty(sent.Users);
        Assert.Equal("Staff area", sent.Realm);
    }

    [Fact]
    public async Task TheSatisfyChoice_UsesLabelsThatSayWhatTheVisitorMustPass()
    {
        await using DashboardTestContext context = new();
        ConfigPages.SignIn(context, Role.Operator).Document = new ConfigDocument(AccessLists: [Office]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<AccessLists>(parameters => parameters.Add(p => p.Key, Office.Id.ToString()));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-field=satisfy]").Count == 1);

        var select = drawers.FindComponents<MudSelect<AccessListSatisfy>>().Single();

        Assert.Equal("A visitor must pass", select.Instance.Label);
        Assert.Contains("gets in without a password; any other visitor, a blocked country included, can still sign in", drawers.Find("[data-drawer=access-list]").TextContent, StringComparison.Ordinal);
        Assert.Equal(["Address and password", "Address or password"], new[] { AccessListSatisfy.All, AccessListSatisfy.Any }.Select(Paduraksa.Dashboard.Config.AccessListText.SatisfyText));
    }

    [Fact]
    public async Task TheForm_SaysWhatIsWrongAtTheFieldsWithoutHashingAnything()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(AccessLists: [Guests]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<AccessLists>(parameters => parameters.Add(p => p.Key, DashboardRoutes.NewSegment));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=access-list] input[data-field=name]").Count == 1);
        await TypeAsync(drawers, "[data-drawer=access-list] input[data-field=name]", "guests");
        await TypeAsync(drawers, "[data-drawer=access-list] textarea[data-field=allowed]", "10.0.0.0/8\n10.0.0.1/8");
        await drawers.Find("[data-action=add-user]").ClickAsync(new());
        await drawers.Find("[data-action=add-user]").ClickAsync(new());
        await TypeAsync(drawers, "[data-drawer=access-list] input[data-field=username-0]", "alice");
        await TypeAsync(drawers, "[data-drawer=access-list] input[data-field=username-1]", "alice");
        await TypeAsync(drawers, "[data-drawer=access-list] input[data-field=password-0]", "fine");

        _ = drawers.Find("form[data-drawer=access-list]").SubmitAsync();
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-form=errors]").Count == 1);

        var text = drawers.Find("[data-drawer=access-list]").TextContent;
        Assert.Contains("is already used", text, StringComparison.Ordinal);                   // the name, case-insensitively
        Assert.Contains("10.0.0.1/8", text, StringComparison.Ordinal);                         // the address, by its text
        Assert.Contains("is listed twice", text, StringComparison.Ordinal);                    // the repeated username
        Assert.Contains("Give this user a password.", text, StringComparison.Ordinal);        // the second user has none
        Assert.DoesNotContain("must be hashed", text, StringComparison.Ordinal);               // a typed password is not an error
        Assert.Empty(config.Executed);
    }

    [Fact]
    public async Task DeletingAnUnusedList_AsksFirstAndSendsTheStoredVersion()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        var stored = Guests with { Version = 4 };
        config.Document = new ConfigDocument(AccessLists: [stored]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        var page = context.Render<AccessLists>();

        await page.WaitForStateAsync(() => page.FindAll("[data-action=delete]").Count == 1);
        var deleting = page.Find("[data-action=delete]").ClickAsync(new());
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=apply]").Count == 1);

        var dry = Assert.IsType<DeleteAccessListCommand>(config.Executed[0]);
        Assert.True(dry.DryRun);
        Assert.Equal((stored.Id, 4L), (dry.AccessListId, dry.Version));

        await drawers.Find("[data-review=apply]").ClickAsync(new());
        await deleting.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
        await drawers.WaitForStateAsync(() => config.Executed.Count == 2);
        Assert.False(Assert.IsType<DeleteAccessListCommand>(config.Executed[1]).DryRun);
    }

    [Fact]
    public async Task AReadOnlyRole_SeesTheListButCannotChangeIt()
    {
        await using DashboardTestContext context = new();
        ConfigPages.SignIn(context, Role.Viewer).Document = new ConfigDocument(AccessLists: [Office]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<AccessLists>(parameters => parameters.Add(p => p.Key, Office.Id.ToString()));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-banner=read-only]").Count == 1);

        Assert.Empty(drawers.FindAll("[data-action=add-user]"));
        Assert.Empty(drawers.FindAll("[data-action=generate-0]"));
        Assert.Empty(drawers.FindAll("[data-action=review]"));
    }

    private static AccessListSettings Use(AccessListId list) => new() { AccessListId = list };

    /// <summary>Types into a MudBlazor field and leaves it (which sends what was typed at once, instead of after the field's debounce).</summary>
    private static async Task TypeAsync(IRenderedComponent<DrawerHost> drawers, string selector, string value)
    {
        await drawers.Find(selector).InputAsync(new() { Value = value });
        await drawers.Find(selector).BlurAsync(new());
    }
}
