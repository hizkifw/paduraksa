using System.Reflection;

using Bunit;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using MudBlazor;

using Paduraksa.Core.Hosting;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.AboutArea;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.Layout;
using Paduraksa.Dashboard.Pages;
using Paduraksa.Tests.Dashboard.Components;

namespace Paduraksa.Tests.Dashboard.AboutArea;

public sealed class AboutPageTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static readonly DateTimeOffset Started = new(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);

    private const string NoticesJson = """
        {
          "components": [
            { "name": "Alpha", "version": "1.2.3", "license": "MIT", "copyright": "Copyright (c) Ada", "url": "https://github.com/ada/alpha", "kind": "package" },
            { "name": "Beta", "version": "4.5.6", "license": "MIT", "copyright": "Copyright (c) Bob", "url": "https://example.org/beta/", "kind": "package" },
            { "name": ".NET runtime", "version": "10.0", "license": "Apache-2.0", "copyright": "Copyright (c) Runtime team", "noticesUrl": "https://example.org/notices", "kind": "runtime" },
            { "name": "Icons", "license": "Apache-2.0", "copyright": "Copyright Icons Inc", "kind": "bundled" }
          ],
          "licenses": {
            "MIT": { "name": "MIT License", "text": "MIT TEXT" },
            "Apache-2.0": { "name": "Apache License 2.0", "text": "APACHE TEXT" }
          }
        }
        """;

    /// <summary>The page's services; <paramref name="live"/> signs a Viewer in on an interactive renderer, where the drawer opens (the services are fixed once that is done, so it comes last).</summary>
    private static DashboardTestContext Context(BuildInfo? build = null, string notices = NoticesJson, bool live = false, bool embedded = false)
    {
        DashboardTestContext context = new();
        context.Services.AddSingleton(build ?? new BuildInfo("0.1.0", Sha, new DateTimeOffset(2026, 11, 14, 8, 30, 15, TimeSpan.Zero), BuiltAtHasTime: true));
        context.Services.AddSingleton(new ServerFacts(".NET 10.0.7", "Linux 6.8.0 Ubuntu", "x64", Started));
        context.Services.AddSingleton(embedded ? ThirdPartyNotices.Embedded : ThirdPartyNotices.Parse(System.Text.Json.JsonDocument.Parse(notices).RootElement));
        context.Services.AddSingleton(new ProductLicense("APACHE LICENSE TEXT\n", "Paduraksa\nCopyright 2026 Hizkia Felix\n"));
        ManualTimeProvider time = new(Started + TimeSpan.FromMinutes(135));
        context.Services.AddSingleton<TimeProvider>(time);
        if (live)
        {
            ConfigPages.SignIn(context, Role.Viewer, time);
        }
        else
        {
            context.SetRendererInfo(new RendererInfo("Static", isInteractive: false));
        }

        return context;
    }

    [Fact]
    public async Task TheIdentityCardNamesTheBuild_WithTheCommitLinkedAndCopyable()
    {
        await using var context = Context();
        context.JSInterop.Setup<bool>("paduraksa.copyText", Sha).SetResult(true);

        var page = context.Render<About>();

        var card = page.Find("[data-card=identity]");
        Assert.Equal("Paduraksa", card.QuerySelector("h1")!.TextContent);
        Assert.NotNull(card.QuerySelector("svg.pdk-mark"));
        Assert.Equal("0.1.0", card.QuerySelector("[data-fact=version]")!.TextContent);
        var link = card.QuerySelector("[data-link=commit]")!;
        Assert.Equal("0123456", link.TextContent);
        Assert.Equal($"https://github.com/hizkifw/paduraksa/commit/{Sha}", link.GetAttribute("href"));
        Assert.Equal("_blank", link.GetAttribute("target"));
        Assert.Equal("noopener noreferrer", link.GetAttribute("rel"));
        Assert.Equal("14 Nov 2026, 08:30 UTC", card.QuerySelector("[data-fact=built]")!.TextContent);
        Assert.DoesNotContain(Sha, card.TextContent, StringComparison.Ordinal);

        await card.QuerySelector("[data-action=copy-commit]")!.ClickAsync(new());

        Assert.Equal("Copied", page.Find("[data-action=copy-commit]").GetAttribute("aria-label"));
        Assert.Single(context.JSInterop.Invocations, i => i.Identifier == "paduraksa.copyText");
    }

    [Fact]
    public async Task ABuildWithoutACommitSaysSo_AndOffersNothingToLinkOrCopy()
    {
        await using var context = Context(new BuildInfo("0.1.0", null));

        var page = context.Render<About>();

        var card = page.Find("[data-card=identity]");
        Assert.Equal("Built without a commit ID", card.QuerySelector("[data-fact=commit]")!.TextContent);
        Assert.Empty(card.QuerySelectorAll("a"));
        Assert.Empty(card.QuerySelectorAll("[data-action=copy-commit]"));
        Assert.Empty(card.QuerySelectorAll("[data-fact=built]"));
        Assert.Equal("https://github.com/hizkifw/paduraksa/blob/main/CHANGELOG.md", page.Find("[data-link=changelog]").GetAttribute("href"));
    }

    [Fact]
    public async Task ALocalBuildShowsItsDay_WithoutShiftingItIntoAnotherTimeZone()
    {
        await using var context = Context(new BuildInfo("0.1.0", null, new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero)));
        context.Services.GetRequiredService<DisplayFormat>().Use(new DisplayPreferences(TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"), System.Globalization.CultureInfo.InvariantCulture));

        var page = context.Render<About>();

        Assert.Equal("14 Nov 2026", page.Find("[data-fact=built]").TextContent);
    }

    [Fact]
    public async Task TheFactsAreTheApiTheRuntimeTheSystemAndUptime_TheStartTimeBeingUptimesTooltip()
    {
        await using var context = Context();

        var page = context.Render<About>();

        var facts = page.Find("[data-facts=runtime]");
        Assert.Equal("v1", facts.QuerySelector("[data-fact=api]")!.TextContent);
        Assert.Equal(".NET 10.0.7", facts.QuerySelector("[data-fact=dotnet]")!.TextContent);
        Assert.Equal(["API", "Runtime", "System", "Uptime"], facts.QuerySelectorAll("dt").Select(label => label.TextContent));
        Assert.Equal("Linux 6.8.0 Ubuntu (x64)", facts.QuerySelector("[data-fact=system]")!.TextContent);
        var uptime = facts.QuerySelector("[data-fact=uptime]")!;
        Assert.Equal("2 h 15 min", uptime.TextContent);
        Assert.Equal("Started 2026-11-14 08:00:00 UTC", uptime.GetAttribute("title"));
        Assert.DoesNotContain("Started", facts.TextContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(40, "40 s")]
    [InlineData(12 * 60 + 5, "12 min")]
    [InlineData(3 * 3600 + 25 * 60, "3 h 25 min")]
    [InlineData(3 * 86400 + 4 * 3600 + 59 * 60, "3 d 4 h")]
    public void UptimeNamesItsTwoLargestUnits(int seconds, string expected) =>
        Assert.Equal(expected, ServerFacts.Uptime(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public async Task TheLinksOpenInANewTab_AndTheChangelogIsTheOneAtThisCommit()
    {
        await using var context = Context();

        var page = context.Render<About>();

        var links = page.FindAll("[data-section=links] a");
        Assert.Equal(["Documentation", "Source", "Changelog", "API reference"], links.Select(link => link.TextContent.Trim()));
        Assert.Equal(
        [
            "https://www.paduraksa.org/",
            "https://github.com/hizkifw/paduraksa",
            $"https://github.com/hizkifw/paduraksa/blob/{Sha}/CHANGELOG.md",
            DashboardRoutes.ApiReference,
        ], links.Select(link => link.GetAttribute("href")));
        Assert.All(links, link =>
        {
            Assert.Equal("_blank", link.GetAttribute("target"));
            Assert.Equal("noopener noreferrer", link.GetAttribute("rel"));
        });
    }

    [Fact]
    public async Task TheLicenseIsOneLine_ThatDoesNotRepeatTheWordCopyright_WithTheButtonThatOpensTheDrawer()
    {
        await using var context = Context();

        var page = context.Render<About>();

        Assert.Equal("Apache-2.0 · © 2026 Hizkia Felix", page.Find("[data-fact=license]").TextContent);
        var button = page.Find("[data-action=licenses]");
        Assert.Equal("Open-source licenses (4)", button.TextContent.Trim());
        Assert.Equal(DashboardRoutes.AboutLicenses, button.GetAttribute("href"));
        Assert.Empty(page.FindAll("[data-component]"));
    }

    [Fact]
    public async Task TheLicensesAddress_OpensTheDrawer_WithTheProductTextsAndEveryComponentOfTheFile()
    {
        await using var context = Context(live: true);
        var drawers = context.Render<DrawerHost>();

        context.Render<About>(parameters => parameters.Add(p => p.Key, DashboardRoutes.LicensesSegment));

        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=licenses]").Count == 1);
        Assert.Equal("Open-source licenses", drawers.Find(".pdk-drawer-title").TextContent);
        Assert.Empty(drawers.FindAll("[data-text=license]"));
        await drawers.Find("[data-disclosure=license] button").ClickAsync(new());
        await drawers.Find("[data-disclosure=notice] button").ClickAsync(new());
        Assert.Equal("APACHE LICENSE TEXT", drawers.Find("[data-text=license]").TextContent);
        Assert.Equal("Paduraksa\nCopyright 2026 Hizkia Felix", drawers.Find("[data-text=notice]").TextContent);

        var rows = drawers.FindAll("[data-section=third-party] [data-component]");
        Assert.Equal(["Alpha", "Beta", ".NET runtime", "Icons"], rows.Select(row => row.GetAttribute("data-component")));
        Assert.Contains("1.2.3", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Copyright (c) Ada", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Copyright (c) Bob", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Equal("github.com/ada/alpha", rows[0].QuerySelector("[data-link=project]")!.TextContent);
        Assert.Equal("example.org/beta", rows[1].QuerySelector("[data-link=project]")!.TextContent);
        Assert.Equal("https://example.org/notices", rows[2].QuerySelector("[data-link=notices]")!.GetAttribute("href"));
        Assert.Empty(rows[3].QuerySelectorAll("a"));
        Assert.All(drawers.FindAll("[data-section=third-party] a"), link => Assert.Equal("noopener noreferrer", link.GetAttribute("rel")));
    }

    [Fact]
    public async Task AComponentsLicenseTextOpensUnderIt_TheSameTextForEveryComponentOfThatLicense()
    {
        await using var context = Context();
        var drawer = context.Render<LicensesDrawer>();
        Assert.Empty(drawer.FindAll(".pdk-oss-item .pdk-license-text"));

        await drawer.Find("[data-component=Alpha] button").ClickAsync(new());
        await drawer.Find("[data-component=Beta] button").ClickAsync(new());

        Assert.Equal(["MIT TEXT", "MIT TEXT"], drawer.FindAll(".pdk-oss-item .pdk-license-text").Select(text => text.TextContent));
        Assert.Empty(drawer.Find("[data-component=Icons]").QuerySelectorAll(".pdk-license-text"));
    }

    [Fact]
    public async Task TheSearchNarrowsTheListByNameLicenseOrCopyright_AndSaysWhenNothingMatches()
    {
        await using var context = Context();
        var drawer = context.Render<LicensesDrawer>();

        await drawer.Find(".pdk-search input").InputAsync("apache");
        Assert.Equal([".NET runtime", "Icons"], drawer.FindAll("[data-component]").Select(row => row.GetAttribute("data-component")));
        Assert.Contains("2 components of 4", drawer.Find(".pdk-toolbar-count").TextContent, StringComparison.Ordinal);

        await drawer.Find(".pdk-search input").InputAsync("bob");
        Assert.Equal(["Beta"], drawer.FindAll("[data-component]").Select(row => row.GetAttribute("data-component")));

        await drawer.Find(".pdk-search input").InputAsync("zzz");
        Assert.Empty(drawer.FindAll("[data-component]"));
        Assert.Equal("Nothing matches.", drawer.Find("[data-empty=search]").TextContent);
    }

    [Theory]
    [InlineData("https://example.org:8443/a/b/?x=1#f", "example.org:8443/a/b")]
    [InlineData("http://example.org/", "example.org")]
    [InlineData("not a url", "not a url")]
    public async Task AProjectLinksTextIsItsAddressWithoutTheSchemeQueryOrFragment_OrTheTextAsGivenWhenItIsNoAddress(string url, string shown)
    {
        await using var context = Context(notices: $$"""
            { "components": [ { "name": "X", "license": "MIT", "copyright": "c", "url": "{{url}}", "kind": "package" } ],
              "licenses": { "MIT": { "name": "MIT License", "text": "t" } } }
            """);

        var drawer = context.Render<LicensesDrawer>();

        var link = drawer.Find("[data-link=project]");
        Assert.Equal(shown, link.TextContent);
        Assert.Equal(url, link.GetAttribute("href"));
    }

    [Fact]
    public async Task AnOpenLicenseTextStaysWithItsComponentWhenTheSearchChangesTheList()
    {
        await using var context = Context();
        var drawer = context.Render<LicensesDrawer>();
        await drawer.Find("[data-component=Beta] button").ClickAsync(new());

        await drawer.Find(".pdk-search input").InputAsync("e");

        Assert.Single(drawer.FindAll(".pdk-oss-item .pdk-license-text"));
        Assert.Single(drawer.Find("[data-component=Beta]").QuerySelectorAll(".pdk-license-text"));
        await drawer.Find(".pdk-search input").InputAsync("alpha");
        Assert.Empty(drawer.FindAll(".pdk-oss-item .pdk-license-text"));
    }

    [Fact]
    public async Task AnAddressAfterAboutThatIsNotTheLicensesOpensANotFoundDrawer()
    {
        await using var context = Context(live: true);
        var drawers = context.Render<DrawerHost>();

        context.Render<About>(parameters => parameters.Add(p => p.Key, "nonsense"));

        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=not-found]").Count == 1);
    }

    [Fact]
    public async Task TheEmbeddedNoticesFile_ListsEveryComponentItHolds()
    {
        await using var context = Context(embedded: true);

        var drawer = context.Render<LicensesDrawer>();

        Assert.NotEmpty(ThirdPartyNotices.Embedded.Components);
        Assert.Equal(
            ThirdPartyNotices.Embedded.Components.Select(component => component.Name),
            drawer.FindAll("[data-section=third-party] [data-component]").Select(row => row.GetAttribute("data-component")));
    }

    [Fact]
    public void TheEmbeddedLicenseIsApacheAndNamesTheHolder()
    {
        var license = ProductLicense.Embedded;

        Assert.StartsWith("Apache License", license.LicenseText.TrimStart(), StringComparison.Ordinal);
        Assert.Equal("Copyright 2026 Hizkia Felix", license.Copyright);
        Assert.Equal("2026 Hizkia Felix", license.Holder);
    }

    [Fact]
    public void ANoticeNamingALicenseWithoutTextIsAnError() =>
        Assert.Throws<InvalidOperationException>(() => ThirdPartyNotices.Parse(System.Text.Json.JsonDocument.Parse(
            """{ "components": [ { "name": "X", "license": "GPL", "copyright": "c", "kind": "package" } ], "licenses": {} }""").RootElement));

    [Theory]
    [InlineData(Role.Viewer)]
    [InlineData(Role.Auditor)]
    [InlineData(Role.Operator)]
    [InlineData(Role.Owner)]
    public async Task EverySignedInUserSeesTheAboutPage_AndTheUserMenuLinksToIt(Role role)
    {
        // Only the router's plain [Authorize] stands in front of the page: no role and no policy.
        var authorize = Assert.Single(typeof(About).GetCustomAttributes<AuthorizeAttribute>());
        Assert.Null(authorize.Roles);
        Assert.Null(authorize.Policy);
        Assert.Contains(typeof(About).GetCustomAttributes<Microsoft.AspNetCore.Components.RouteAttribute>(), route => route.Template == DashboardRoutes.About);
        await using var context = Context();
        var popovers = context.Render<MudPopoverProvider>();

        var menu = context.Render<UserMenu>(parameters => parameters.AddCascadingValue(DashboardTestContext.StateOf(TestUsers.User(role))));
        await menu.Find(".pdk-user-chip").ClickAsync(new());

        var item = popovers.FindAll("a.mud-menu-item").Single(a => a.TextContent.Contains("About Paduraksa", StringComparison.Ordinal));
        Assert.Equal(DashboardRoutes.About, item.GetAttribute("href"));
        Assert.Contains("0.1.0", context.Render<About>().Find("[data-fact=version]").TextContent, StringComparison.Ordinal);
    }
}
