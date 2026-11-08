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

/// <summary>
/// The overview says what needs the person and where to fix it (PLAN §12), and the navigation and the areas' tabs mark where a page
/// belongs: a profile with the hosts, an ACME account or DNS provider with the certificates, My account with the settings.
/// </summary>
public sealed class OverviewAndNavigationTests
{
    [Fact]
    public async Task TheOverview_ListsWhatNeedsThePerson_WithWhereToFixIt()
    {
        await using DashboardTestContext context = new();
        FakeDashboardConfig config = ConfigPages.SignIn(context, Role.Operator);
        UpstreamConfig off = new(UpstreamId.New(), "media", Destinations: [new DestinationConfig(DestinationId.New(), "http://10.0.0.9:8096", Enabled: false)]);
        HostConfig host = new(HostId.New(), ["media.home.test"], Routes: [new RouteConfig(RouteId.New(), off.Id)]);
        config.Document = new ConfigDocument(Upstreams: [off], Hosts: [host]);
        config.LiveWarnings = [new ConfigWarning("/hosts/0", "certificate.auto.noCoverage", "no valid certificate covers media.home.test")];
        context.ComponentFactories.AddStub<SetupChecklist>();

        IRenderedComponent<Home> page = context.Render<Home>();

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
    public async Task TheOverview_SaysWhenNothingNeedsThePerson()
    {
        await using DashboardTestContext context = new();
        ConfigPages.SignIn(context, Role.Viewer).Document = new ConfigDocument();
        context.ComponentFactories.AddStub<SetupChecklist>();

        IRenderedComponent<Home> page = context.Render<Home>();

        await page.WaitForStateAsync(() => page.FindAll("[data-state=all-good]").Count == 1);
        Assert.Contains("No hosts yet", page.Find("[data-section=hosts]").TextContent, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-action=add-host]"));
    }

    [Theory]
    [InlineData("/settings/profiles/prof_0J9zb3xQk1Lr8m2V7aYc4D", "Hosts")]
    [InlineData("/hosts/new", "Hosts")]
    [InlineData("/settings/acme-accounts", "Certificates")]
    [InlineData("/settings/users", "Settings")]
    [InlineData("/account", "Settings")]
    [InlineData("/", "Overview")]
    public void TheNavigation_MarksTheAreaAPageBelongsTo(string path, string area)
    {
        using DashboardTestContext context = new();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(path);

        IRenderedComponent<NavMenu> menu = context.Render<NavMenu>(parameters =>
            parameters.AddCascadingValue(DashboardTestContext.StateOf(TestUsers.User(Role.Owner))));

        Assert.Equal(area, Assert.Single(menu.FindAll(".pdk-nav-active .pdk-nav-label")).TextContent);
    }

    [Theory]
    [InlineData(SectionArea.Certificates, "/settings/dns-providers", Role.Owner, new[] { "Certificates", "ACME accounts", "DNS providers" }, "DNS providers")]
    [InlineData(SectionArea.Traffic, "/settings/profiles", Role.Viewer, new[] { "Hosts", "Upstreams", "Profiles" }, "Profiles")]
    [InlineData(SectionArea.Settings, "/settings", Role.Viewer, new[] { "All settings", "Global defaults", "API tokens", "My account" }, "All settings")]
    public void AnAreasTabs_OfferWhatTheRoleMaySee_AndMarkTheOpenPage(SectionArea area, string path, Role role, string[] tabs, string active)
    {
        using DashboardTestContext context = new();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(path);

        IRenderedComponent<SectionTabs> rendered = context.Render<SectionTabs>(parameters => parameters
            .AddCascadingValue(DashboardTestContext.StateOf(TestUsers.User(role)))
            .Add(t => t.Area, area));

        Assert.Equal(tabs, rendered.FindAll(".pdk-tab").Select(t => t.TextContent));
        Assert.Equal(active, rendered.Find(".pdk-tab-active").TextContent);
    }
}
