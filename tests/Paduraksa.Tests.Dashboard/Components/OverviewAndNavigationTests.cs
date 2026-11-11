using Bunit;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
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
    [InlineData(Role.Owner, new[] { "Global defaults", "Users and roles", "API tokens" })]
    [InlineData(Role.Viewer, new[] { "Global defaults", "API tokens" })]
    public void TheSettingsGroup_ListsThePagesTheRoleMaySee(Role role, string[] entries)
    {
        using DashboardTestContext context = new();

        var menu = context.Render<NavMenu>(parameters =>
            parameters.AddCascadingValue(DashboardTestContext.StateOf(TestUsers.User(role))));

        string[] all = ["Global defaults", "Users and roles", "API tokens"];
        Assert.Equal(entries, menu.FindAll(".pdk-nav-label").Select(label => label.TextContent).Where(all.Contains));
    }
}
