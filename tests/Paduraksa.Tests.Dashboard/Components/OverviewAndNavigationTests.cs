using Bunit;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Components;
using Paduraksa.Dashboard.Layout;
using Paduraksa.Dashboard.Pages;
using Paduraksa.Dashboard.Setup;

namespace Paduraksa.Tests.Dashboard.Components;

public sealed class OverviewAndNavigationTests
{
    [Fact]
    public async Task TheOverview_ListsWhatNeedsThePerson_WithWhereToFixIt()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        UpstreamConfig off = new(UpstreamId.New(), "media", Destinations: [new DestinationConfig(DestinationId.New(), "http://10.0.0.9:8096", Enabled: false)]);
        HostConfig host = new(HostId.New(), ["media.home.test"], Routes: [new RouteConfig(RouteId.New(), off.Id)]);
        config.Document = new ConfigDocument(Upstreams: [off], Hosts: [host]);
        config.LiveWarnings = [new ConfigWarning("/hosts/0", "certificate.auto.noCoverage", "no valid certificate covers media.home.test")];
        context.ComponentFactories.AddStub<SetupChecklist>();
        context.ComponentFactories.AddStub<TrafficOverview>();

        var page = context.Render<Home>();

        await page.WaitForStateAsync(() => page.FindAll(".pdk-attention-item").Count == 2);
        // The broken one first: the upstream with nothing switched on, then the host the live config warns about.
        Assert.Equal(["error", "warning"], page.FindAll(".pdk-attention-item").Select(i => i.GetAttribute("data-tone")));
        Assert.Contains("media has no destination switched on", page.Find("[data-section=attention]").TextContent, StringComparison.Ordinal);
        Assert.Equal(DashboardRoutes.Upstream(off.Id), page.FindAll(".pdk-attention-item a")[0].GetAttribute("href"));
        Assert.Equal(DashboardRoutes.Host(host.Id), page.FindAll(".pdk-attention-item a")[1].GetAttribute("href"));
        Assert.Contains("No valid certificate covers media.home.test", page.Find("[data-section=attention]").TextContent, StringComparison.Ordinal);
        Assert.Contains("media.home.test", page.Find("[data-section=hosts]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOverview_ShowsNoAttentionCardWhenNothingNeedsThePerson()
    {
        await using DashboardTestContext context = new();
        ConfigPages.SignIn(context, Role.Viewer).Document = new ConfigDocument();
        context.ComponentFactories.AddStub<SetupChecklist>();
        context.ComponentFactories.AddStub<TrafficOverview>();

        var page = context.Render<Home>();

        await page.WaitForStateAsync(() => page.Find("[data-section=hosts]").TextContent.Contains("No hosts yet", StringComparison.Ordinal));
        Assert.Empty(page.FindAll("[data-section=attention]"));
        Assert.Empty(page.FindAll("[data-action=add-host]"));
    }

    [Fact]
    public async Task TheOverview_WarnsTheOwnerWhenTheMasterKeySitsBesideTheVault_WithALinkToTheGuide()
    {
        await using DashboardTestContext context = new();
        context.Services.AddSingleton<IVaultLifecycle>(new FakeVaultLifecycle(KekSourceKind.GeneratedFile, besideDatabase: true));
        ConfigPages.SignIn(context, Role.Owner).Document = new ConfigDocument();
        context.ComponentFactories.AddStub<SetupChecklist>();
        context.ComponentFactories.AddStub<TrafficOverview>();

        var page = context.Render<Home>();

        await page.WaitForStateAsync(() => page.FindAll(".pdk-attention-item").Count == 1);
        var item = page.Find(".pdk-attention-item");
        Assert.Equal("warning", item.GetAttribute("data-tone"));
        Assert.Contains("The master key is stored beside the vault", item.TextContent, StringComparison.Ordinal);
        Assert.Contains("A copy of the data directory is a copy of every certificate key and secret", item.TextContent, StringComparison.Ordinal);
        var link = item.QuerySelector("a")!;
        Assert.EndsWith("/docs/install.md#the-master-key", link.GetAttribute("href"), StringComparison.Ordinal);
        Assert.Equal("_blank", link.GetAttribute("target"));
        Assert.Contains("noopener", link.GetAttribute("rel"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Role.Owner, KekSourceKind.File, false)]
    [InlineData(Role.Owner, KekSourceKind.Environment, false)]
    [InlineData(Role.Admin, KekSourceKind.GeneratedFile, true)]
    [InlineData(Role.Operator, KekSourceKind.GeneratedFile, true)]
    public async Task TheOverview_SaysNothingAboutTheMasterKeyWhenItIsElsewhere_OrTheRoleCannotManageTheVault(Role role, KekSourceKind source, bool beside)
    {
        await using DashboardTestContext context = new();
        context.Services.AddSingleton<IVaultLifecycle>(new FakeVaultLifecycle(source, beside));
        ConfigPages.SignIn(context, role).Document = new ConfigDocument();
        context.ComponentFactories.AddStub<SetupChecklist>();
        context.ComponentFactories.AddStub<TrafficOverview>();

        var page = context.Render<Home>();

        await page.WaitForStateAsync(() => page.Find("[data-section=hosts]").TextContent.Contains("No hosts yet", StringComparison.Ordinal));
        Assert.Empty(page.FindAll("[data-section=attention]"));
    }

    [Theory]
    [InlineData(Role.Owner)]
    [InlineData(Role.Admin)]
    public async Task TheOverview_LeavesTheViewersOwnSecondFactorToTheSetupChecklist(Role role)
    {
        await using DashboardTestContext context = new();
        IdentityPages.SignIn(context, role).Config.Document = new ConfigDocument();
        context.ComponentFactories.AddStub<SetupChecklist>();
        context.ComponentFactories.AddStub<TrafficOverview>();

        var page = context.Render<Home>();

        await page.WaitForStateAsync(() => page.Find("[data-section=hosts]").TextContent.Contains("No hosts yet", StringComparison.Ordinal));
        Assert.Empty(page.FindAll("[data-section=attention]"));
        Assert.Empty(page.FindAll("[data-section=hints]"));
    }

    [Fact]
    public async Task TheOverview_SaysNothingOnceTheViewerHasASecondFactor_AndNamesOtherAdministratorsWhoHaveNone()
    {
        await using DashboardTestContext context = new();
        var (identity, _, config, _) = IdentityPages.SignIn(context, Role.Owner);
        config.Document = new ConfigDocument();
        identity.Users[0] = identity.Users[0] with { TwoFactorEnabled = true };
        identity.Users.Add(FakeDashboardIdentity.User("grace", Role.Admin));
        identity.Users.Add(FakeDashboardIdentity.User("linus", Role.Operator));
        identity.Users.Add(FakeDashboardIdentity.User("ken", Role.Viewer));
        identity.Users.Add(FakeDashboardIdentity.User("dennis", Role.Admin) with { Disabled = true });
        identity.Users.Add(FakeDashboardIdentity.User("barbara", Role.Admin) with { TwoFactorEnabled = true });
        context.ComponentFactories.AddStub<SetupChecklist>();
        context.ComponentFactories.AddStub<TrafficOverview>();

        var page = context.Render<Home>();

        await page.WaitForStateAsync(() => page.FindAll(".pdk-hint").Count == 1);
        var item = page.Find(".pdk-hint");
        Assert.Contains("grace has no second factor", item.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("linus", item.TextContent, StringComparison.Ordinal);
        Assert.Equal(SettingsRoutes.Users, item.QuerySelector("a")!.GetAttribute("href"));
    }

    [Fact]
    public async Task TheOverview_CountsSeveralAdministratorsWithoutASecondFactorInOneLine()
    {
        await using DashboardTestContext context = new();
        var (identity, _, config, _) = IdentityPages.SignIn(context, Role.Owner);
        config.Document = new ConfigDocument();
        identity.Users[0] = identity.Users[0] with { TwoFactorEnabled = true };
        foreach (var name in new[] { "grace", "linus", "ken", "dennis" })
        {
            identity.Users.Add(FakeDashboardIdentity.User(name, Role.Admin));
        }

        context.ComponentFactories.AddStub<SetupChecklist>();
        context.ComponentFactories.AddStub<TrafficOverview>();

        var page = context.Render<Home>();

        await page.WaitForStateAsync(() => page.FindAll(".pdk-hint").Count == 1);
        var text = page.Find(".pdk-hint").TextContent;
        Assert.Contains("4 other administrators have no second factor (grace, linus, ken and more)", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Role.Operator)]
    [InlineData(Role.Viewer)]
    [InlineData(Role.Auditor)]
    public async Task TheOverview_DoesNotTellARoleThatCannotReadUsersAboutSecondFactors(Role role)
    {
        await using DashboardTestContext context = new();
        var (identity, _, config, _) = IdentityPages.SignIn(context, role);
        config.Document = new ConfigDocument();
        identity.Users.Add(FakeDashboardIdentity.User("grace", Role.Owner));
        context.ComponentFactories.AddStub<SetupChecklist>();
        context.ComponentFactories.AddStub<TrafficOverview>();

        var page = context.Render<Home>();

        await page.WaitForStateAsync(() => page.Find("[data-section=hosts]").TextContent.Contains("No hosts yet", StringComparison.Ordinal));
        Assert.Empty(page.FindAll("[data-section=attention]"));
        Assert.Empty(page.FindAll("[data-section=hints]"));
    }

    [Theory]
    [InlineData("/profiles/prof_0J9zb3xQk1Lr8m2V7aYc4D", "Profiles")]
    [InlineData("/security", "Security overview")]
    [InlineData("/rate-limits", "Rate limits")]
    [InlineData("/rate-limits/new", "Rate limits")]
    [InlineData("/access-lists", "Access lists")]
    [InlineData("/access-lists/acl_02wndAAHBC95f4Al8rNFlB", "Access lists")]
    [InlineData("/rules", "Rules")]
    [InlineData("/rules/new", "Rules")]
    [InlineData("/rules/rs_02wndAAHBC95f4Al8rNFlB", "Rules")]
    [InlineData("/hosts/new", "Hosts")]
    [InlineData("/certificates/crt_02x5L6n2fuxI8jmEuqQF0v", "Certificates")]
    [InlineData("/certificates/acme", "ACME accounts")]
    [InlineData("/certificates/local-ca", "Local CA")]
    [InlineData("/settings/users", "Users and roles")]
    [InlineData("/", "Overview")]
    public void TheNavigation_MarksThePageThatIsOpen(string path, string entry)
    {
        using DashboardTestContext context = new();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(path);

        var menu = context.Render<NavMenu>(parameters =>
            parameters.AddCascadingValue(DashboardTestContext.StateOf(TestUsers.User(Role.Owner))));

        Assert.Equal(entry, Assert.Single(menu.FindAll(".pdk-nav-active .pdk-nav-label")).TextContent);
    }

    [Fact]
    public void TheNavigation_MarksNothingForTheAccount_WhichIsInTheAccountMenu()
    {
        using DashboardTestContext context = new();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardRoutes.Account);

        var menu = context.Render<NavMenu>(parameters =>
            parameters.AddCascadingValue(DashboardTestContext.StateOf(TestUsers.User(Role.Owner))));

        Assert.Empty(menu.FindAll(".pdk-nav-active"));
    }

    [Theory]
    [InlineData(Role.Owner, new[] { "Global defaults", "Users and roles", "API tokens", "Backup" })]
    [InlineData(Role.Viewer, new[] { "Global defaults", "API tokens" })]
    public void TheSettingsGroup_ListsThePagesTheRoleMaySee(Role role, string[] entries)
    {
        using DashboardTestContext context = new();

        var menu = context.Render<NavMenu>(parameters =>
            parameters.AddCascadingValue(DashboardTestContext.StateOf(TestUsers.User(role))));

        string[] all = ["Global defaults", "Users and roles", "API tokens", "Backup"];
        Assert.Equal(entries, menu.FindAll(".pdk-nav-label").Select(label => label.TextContent).Where(all.Contains));
    }
}
