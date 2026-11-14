using Bunit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Paduraksa.Application.Security;
using Paduraksa.Contracts.Security;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Layout;
using Paduraksa.Dashboard.Pages;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.ForwardAuth;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Metrics;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.RateLimiting;
using Paduraksa.Proxy.RuleSets;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.ProxyProtocol;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Dashboard.Components;

public sealed class SecurityPageTests
{
    private static readonly HostId App = HostId.New();
    private static readonly HostId Files = HostId.New();
    private static readonly HostId Blog = HostId.New();
    private static readonly RateLimitPolicyId Standard = RateLimitPolicyId.New();
    private static readonly RateLimitPolicyId Strict = RateLimitPolicyId.New();
    private static readonly UpstreamId Upstream = UpstreamId.New();
    private static readonly Paduraksa.Core.Vault.IPasswordCredential Credential = TestPasswords.Credential("sample");
    private static readonly AccessListId Office = AccessListId.New();
    private static readonly AccessListId Lan = AccessListId.New();
    private static readonly RuleSetId Scanners = RuleSetId.New();
    private static readonly AuthProviderId Sso = AuthProviderId.New();

    [Fact]
    public async Task TheCloudflareColumnSaysLockdownAndOriginPullsAndCountsAHostWithOnlyThemAsProtected()
    {
        await using DashboardTestContext context = new();
        await using var shell = await StartAsync(context, Role.Operator);
        HostSpec Plain(HostId id, string name, HostCloudflareSpec cloudflare) => new()
        {
            Id = id,
            Names = [name],
            Https = false,
            Cloudflare = cloudflare,
            Routes = [new RouteSpec { Id = RouteId.New(), Upstream = Upstream }],
        };
        shell.Publish(new ProxyConfigModel
        {
            Upstreams = [new UpstreamSpec { Id = Upstream, Destinations = [new DestinationSpec(DestinationId.New(), "http://127.0.0.1:1")] }],
            Defaults = new DefaultsSpec { TrustedProxies = new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareProxy } },
            Hosts =
            [
                Plain(HostId.New(), "z-none.test", new HostCloudflareSpec()),
                Plain(HostId.New(), "b-both.test", new HostCloudflareSpec { OriginLockdown = OriginLockdownMode.Forbid, AuthenticatedOriginPulls = true }),
                Plain(HostId.New(), "c-pulls.test", new HostCloudflareSpec { AuthenticatedOriginPulls = true }),
                Plain(HostId.New(), "a-drop.test", new HostCloudflareSpec { OriginLockdown = OriginLockdownMode.Drop }),
            ],
        });

        var page = context.Render<SecurityPage>();

        // The three with something on come first, by name; the one with nothing last.
        Assert.Equal(["a-drop.test", "b-both.test", "c-pulls.test", "z-none.test"], page.FindAll("[data-host]").Select(row => row.GetAttribute("data-host")));
        string Cell(string host) => string.Join(" | ", page.Find($"[data-host='{host}'] [data-cell=cloudflare]").QuerySelectorAll("span").Select(span => span.TextContent.Trim()));
        Assert.Equal("Cloudflare only (drop)", Cell("a-drop.test"));
        Assert.Equal("Cloudflare only (403) | Origin pulls", Cell("b-both.test"));
        Assert.Equal("Origin pulls", Cell("c-pulls.test"));
        Assert.Equal("None", Cell("z-none.test"));
        Assert.Single(page.FindAll("[data-host='b-both.test'] [data-cell=cloudflare] .pdk-dt-note"));
        Assert.Empty(page.FindAll("[data-host='c-pulls.test'] [data-cell=cloudflare] .pdk-dt-note"));
    }

    [Fact]
    public async Task TheHostsTableListsProtectedHostsByNameThenThoseWithNothing()
    {
        await using DashboardTestContext context = new();
        await using var shell = await StartAsync(context, Role.Operator);
        shell.Publish(Hosts());

        var page = context.Render<SecurityPage>();

        Assert.Equal(["app.test", "blog.test", "files.test"], page.FindAll("[data-host]").Select(row => row.GetAttribute("data-host")));
        var app = page.Find("[data-host='app.test']");
        Assert.Contains("On, 18 bits", app.QuerySelector("[data-cell=pow]")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("Off on 1 route", app.QuerySelector("[data-cell=pow]")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("standard", app.QuerySelector("[data-cell=rate-limit] > .pdk-dt-stack > a")!.TextContent);
        Assert.Equal(DashboardRoutes.RateLimit(Standard), app.QuerySelector("[data-cell=rate-limit] > .pdk-dt-stack > a")!.GetAttribute("href"));
        Assert.Contains("on 1 route", app.QuerySelector("[data-cell=rate-limit]")!.TextContent, StringComparison.Ordinal);
        Assert.Equal(DashboardRoutes.RateLimit(Strict), app.QuerySelectorAll("[data-cell=rate-limit] a")[1].GetAttribute("href"));
        Assert.Equal("Blocks AI crawlers", app.QuerySelector("[data-cell=robots]")!.TextContent.Trim());
        Assert.Equal("office", app.QuerySelector("[data-cell=access-list] > .pdk-dt-stack > a")!.TextContent);
        Assert.Equal(DashboardRoutes.AccessList(Office), app.QuerySelector("[data-cell=access-list] > .pdk-dt-stack > a")!.GetAttribute("href"));
        Assert.Contains("2 users", app.QuerySelector("[data-cell=access-list]")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("on 1 route", app.QuerySelector("[data-cell=access-list]")!.TextContent, StringComparison.Ordinal);
        Assert.Equal(DashboardRoutes.AccessList(Lan), app.QuerySelectorAll("[data-cell=access-list] a")[1].GetAttribute("href"));
        Assert.Equal(DashboardRoutes.Host(App), app.QuerySelector(".pdk-dt-main a")!.GetAttribute("href"));
    }

    [Fact]
    public async Task TheRulesColumnNamesTheSetWhatItRefusedAndTheRoutesThatApplyNoneOrAPreset()
    {
        await using DashboardTestContext context = new();
        await using var shell = await StartAsync(context, Role.Operator);
        shell.Publish(Hosts());
        var counters = shell.RuleSets.CountersFor(Scanners);
        counters.Count(RuleAction.Deny);
        counters.Count(RuleAction.Deny);
        counters.Count(RuleAction.Drop);
        counters.Count(RuleAction.Allow);
        counters.CountTimeout();
        await SettleAsync(shell);

        var page = context.Render<SecurityPage>();

        var app = page.Find("[data-host='app.test']");
        Assert.Equal("scanners", app.QuerySelector("[data-cell=rules] > .pdk-dt-stack > a")!.TextContent);
        Assert.Equal(DashboardRoutes.RuleSet(Scanners), app.QuerySelector("[data-cell=rules] > .pdk-dt-stack > a")!.GetAttribute("href"));
        Assert.Contains("3 refused", app.QuerySelector("[data-cell=rules]")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("1 regex timeout", app.QuerySelector("[data-cell=rules] [data-part=rule-timeouts]")!.TextContent);
        Assert.Empty(page.Find("[data-host='blog.test']").QuerySelectorAll("[data-part=rule-timeouts]"));
        Assert.Contains("Off on 1 route", app.QuerySelector("[data-cell=rules]")!.TextContent, StringComparison.Ordinal);

        // A preset has no page of its own: it links to the list that shows the presets, and says its rules until it has refused anything.
        var blog = page.Find("[data-host='blog.test']");
        Assert.Equal("Block empty user agent", blog.QuerySelector("[data-cell=rules] a")!.TextContent);
        Assert.Equal(DashboardRoutes.RuleSets, blog.QuerySelector("[data-cell=rules] a")!.GetAttribute("href"));
        Assert.Contains("1 rule", blog.QuerySelector("[data-cell=rules]")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSignInColumnNamesTheProviderWhatItDecidedAndTheRoutesThatAskNone()
    {
        await using DashboardTestContext context = new();
        await using var shell = await StartAsync(context, Role.Operator);
        shell.Publish(Hosts());
        var counters = shell.ForwardAuth.CountersFor(Sso);
        for (var i = 0; i < 1204; i++)
        {
            counters.CountAllowed();
        }

        counters.CountDenied();
        counters.CountRedirected();
        counters.CountTimeout();
        await SettleAsync(shell);

        var page = context.Render<SecurityPage>();

        var app = page.Find("[data-host='app.test']");
        var cell = app.QuerySelector("[data-cell=sign-in]")!;
        Assert.Equal("sso", cell.QuerySelector(".pdk-dt-stack > a")!.TextContent);
        Assert.Equal(DashboardRoutes.AuthProvider(Sso), cell.QuerySelector(".pdk-dt-stack > a")!.GetAttribute("href"));
        Assert.Contains("1,204 allowed, 2 refused", cell.TextContent, StringComparison.Ordinal);
        Assert.Equal("1 failed call", cell.QuerySelector("[data-part=sign-in-failures]")!.TextContent);
        Assert.Contains("Off on 1 route", cell.TextContent, StringComparison.Ordinal);
        Assert.Empty(page.Find("[data-host='blog.test']").QuerySelectorAll("[data-part=sign-in-failures]"));
        Assert.Equal("None", page.Find("[data-host='blog.test'] [data-cell=sign-in]").TextContent.Trim());
    }

    [Fact]
    public async Task AHostWithNothingOnShowsMutedNoneInEveryCell()
    {
        await using DashboardTestContext context = new();
        await using var shell = await StartAsync(context, Role.Operator);
        shell.Publish(Hosts());

        var page = context.Render<SecurityPage>();

        var row = page.Find("[data-host='files.test']");
        Assert.Equal("0", row.QuerySelector("[data-cell=refused] .pdk-muted")!.TextContent);
        var cells = row.QuerySelectorAll(".pdk-dt-cell:not([data-cell=refused])");
        Assert.Equal(7, cells.Length);
        Assert.All(cells, cell =>
        {
            Assert.Equal("None", cell.TextContent.Trim());
            Assert.NotNull(cell.QuerySelector(".pdk-muted"));
        });
    }

    [Fact]
    public async Task ARouteOnlyProofOfWorkAndARateLimitOnRoutesOnlyAreSaidSo()
    {
        await using DashboardTestContext context = new();
        await using var shell = await StartAsync(context, Role.Operator);
        shell.Publish(Hosts());

        var page = context.Render<SecurityPage>();

        var blog = page.Find("[data-host='blog.test']");
        Assert.Contains("On for 1 route", blog.QuerySelector("[data-cell=pow]")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("12 bits", blog.QuerySelector("[data-cell=pow]")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("strict on 1 route", string.Concat(blog.QuerySelector("[data-cell=rate-limit]")!.TextContent.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries).Select(word => word + " ")).Trim());
        Assert.Empty(blog.QuerySelectorAll("[data-cell=rate-limit] > .pdk-dt-stack > a"));
    }

    [Fact]
    public async Task WithNoHostsAnOperatorIsOfferedTheWayToAddOne()
    {
        await using DashboardTestContext context = new();
        await using var shell = await StartAsync(context, Role.Operator);

        var page = context.Render<SecurityPage>();

        Assert.Empty(page.FindAll("[data-host]"));
        Assert.Contains("No hosts yet", page.Find("[data-section=hosts]").TextContent, StringComparison.Ordinal);
        Assert.Equal(DashboardRoutes.NewHost, page.Find("[data-action=add-host]").GetAttribute("href"));
    }

    [Fact]
    public async Task AViewerWithNoHostsIsNotOfferedTheLink()
    {
        await using DashboardTestContext context = new();
        await using var shell = await StartAsync(context, Role.Viewer);

        var page = context.Render<SecurityPage>();

        Assert.Contains("No hosts yet", page.Markup, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-action=add-host]"));
    }

    [Fact]
    public async Task TheLineSaysWhatWasRefusedInTheLast24HoursByReasonAndLinksToTheStatistics()
    {
        await using DashboardTestContext context = new();
        await using var shell = await StartAsync(context, Role.Operator);
        shell.Publish(Hosts());
        shell.Refuse(App, GateOutcome.RefusedRateLimit, 900);
        shell.Refuse(Blog, GateOutcome.RefusedAccessList, 250);
        shell.Refuse(App, GateOutcome.RefusedProofOfWork, 54);
        shell.Refuse(App, GateOutcome.NoRoute, 3);
        await SettleAsync(shell);

        var page = context.Render<SecurityPage>();

        var line = page.Find("[data-part=refused]");
        Assert.Equal("Refused in the last 24 h: 1,204", line.QuerySelector("a")!.TextContent);
        Assert.Equal("/analytics?tab=security&range=24h", line.QuerySelector("a")!.GetAttribute("href"));
        Assert.Equal("(rate limit 900 · access list 250 · proof of work 54)", line.QuerySelector("[data-part=reasons]")!.TextContent.Trim());
        Assert.Equal("954", page.Find("[data-host='app.test'] [data-cell=refused]").TextContent.Trim());
        Assert.Equal("250", page.Find("[data-host='blog.test'] [data-cell=refused]").TextContent.Trim());
        Assert.Empty(page.FindAll("[data-tile]"));
        // The page's content is one section, so the title is its heading: the hosts card has none of its own.
        Assert.Empty(page.FindAll("h2"));
    }

    [Fact]
    public async Task NothingRefusedIsSaidSoWithNoReasons()
    {
        await using DashboardTestContext context = new();
        await using var shell = await StartAsync(context, Role.Operator);
        shell.Publish(Hosts());

        var page = context.Render<SecurityPage>();

        Assert.Equal("Nothing refused in the last 24 h", page.Find("[data-part=refused] a").TextContent);
        Assert.Empty(page.FindAll("[data-part=reasons]"));
    }

    [Fact]
    public async Task RollupsThatCantBeReadLeaveTheLiveProtectionsWithoutThe24HourFigures()
    {
        await using DashboardTestContext context = new();
        await using var shell = await StartAsync(context, Role.Operator, database: false);
        shell.Publish(Hosts());

        var page = context.Render<SecurityPage>();

        Assert.Contains("couldn't be read", page.Find("[data-part=failed]").TextContent, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-part=refused]"));
        Assert.Empty(page.FindAll("[data-cell=refused]"));
        Assert.Equal(3, page.FindAll("[data-host]").Count);
        Assert.Contains("pdk-security-table-live", page.Find(".pdk-security-table").ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheProtectionsAreReadAgainWhileTheTabIsVisibleAndNotWhileItIsHidden()
    {
        await using DashboardTestContext context = new();
        await using var shell = await StartAsync(context, Role.Operator);
        var page = context.Render<SecurityPage>(parameters => parameters.Add(p => p.PollInterval, TimeSpan.FromSeconds(2)));
        Assert.Empty(page.FindAll("[data-host]"));

        await page.InvokeAsync(() => page.Instance.PageVisibilityChangedAsync(false));
        shell.Publish(Hosts());
        Advance(shell, TimeSpan.FromSeconds(10));
        await page.InvokeAsync(() => { }); // Whatever the timer queued on the renderer has run once this does.
        Assert.Empty(page.FindAll("[data-host]"));

        await page.InvokeAsync(() => page.Instance.PageVisibilityChangedAsync(true));
        Assert.Equal(3, page.FindAll("[data-host]").Count);

        shell.Publish(Hosts() with { Hosts = [.. Hosts().Hosts.Where(host => host.Id != Files)] });
        Advance(shell, TimeSpan.FromSeconds(2));
        await page.WaitForStateAsync(() => page.FindAll("[data-host]").Count == 2);
    }

    [Fact]
    public void TheNavigationListsTheOverviewFirstInTheSecurityGroup()
    {
        var group = DashboardNavigation.Entries.Where(entry => entry.Group == DashboardNavigation.SecurityGroup).ToList();

        Assert.Equal("Security overview", group[0].Title);
        Assert.Equal(DashboardRoutes.Security, group[0].Href);
        Assert.Equal(Permission.ConfigRead, group[0].Requires);
        Assert.Equal(DashboardNavigation.Entries.Single(entry => entry.Href == DashboardRoutes.Security), DashboardNavigation.ActiveFor("/security", DashboardNavigation.Entries));
    }

    [Fact]
    public void ASolveRateOfNothingServedIsBlank()
    {
        Assert.Null(SecurityFigures.Share(0, 0));
        Assert.Equal("50%", SecurityFigures.Share(1, 2));
    }

    private static async Task<SecurityRig> StartAsync(DashboardTestContext context, Role role, bool database = true)
    {
        SecurityRig shell = new(new SteppedTime(new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero)));
        if (database)
        {
            await shell.UseDatabaseAsync();
        }

        shell.Register(context.Services);
        ConfigPages.SignIn(context, role, shell.Time);
        return shell;
    }

    private static Task SettleAsync(SecurityRig shell) => shell.SettleAsync(((SteppedTime)shell.Time).Advance);

    private static void Advance(SecurityRig shell, TimeSpan by) => ((SteppedTime)shell.Time).Advance(by);

    private static ProxyConfigModel Hosts()
    {
        HostSpec Plain(HostId id, string name) => new()
        {
            Id = id,
            Names = [name],
            Https = false,
            Routes = [new RouteSpec { Id = RouteId.New(), Upstream = Upstream }],
        };

        RouteSpec Route(string path) => new() { Id = RouteId.New(), Upstream = Upstream, Path = path };

        return new ProxyConfigModel
        {
            Upstreams = [new UpstreamSpec { Id = Upstream, Destinations = [new DestinationSpec(DestinationId.New(), "http://127.0.0.1:1")] }],
            RateLimitPolicies =
            [
                new RateLimitPolicySpec { Id = Standard, Name = "standard", Parameters = new RateLimitParameters(10, 20) },
                new RateLimitPolicySpec { Id = Strict, Name = "strict", Parameters = new RateLimitParameters(1, 2) },
            ],
            RuleSets =
            [
                new RuleSetSpec { Id = Scanners, Name = "scanners", Rules = [new RuleSpec { Action = RuleAction.Deny, UserAgentContains = ["sqlmap"] }] },
                new RuleSetSpec { Id = Contracts.Config.RuleSetPresets.EmptyUserAgent.Id, Name = "Block empty user agent", Rules = [new RuleSpec { Action = RuleAction.Deny, UserAgentEmpty = true }] },
            ],
            AuthProviders = [new AuthProviderSpec { Id = Sso, Name = "sso", Url = "http://authelia:9091/api/authz/forward-auth" }],
            AccessLists =
            [
                new AccessListSpec
                {
                    Id = Office,
                    Name = "office",
                    Allow = ["10.0.0.0/8"],
                    Realm = "office",
                    Users = [new AccessListUserSpec("alice", Credential), new AccessListUserSpec("bob", Credential)],
                },
                new AccessListSpec { Id = Lan, Name = "lan", Allow = ["192.168.0.0/16"], Realm = "lan" },
            ],
            Hosts =
            [
                Plain(Files, "files.test"),
                Plain(Blog, "blog.test") with
                {
                    Routes =
                    [
                        Route("/") with { },
                        Route("/login") with
                        {
                            RateLimit = Strict,
                            RateLimitRouteOverride = true,
                            ProofOfWork = new HostProofOfWorkSpec { Enabled = true, Difficulty = 12 },
                            ProofOfWorkRouteOverride = true,
                        },
                    ],
                    ProofOfWork = new HostProofOfWorkSpec { Enabled = false, Difficulty = 12 },
                    RuleSet = new HostRuleSetSpec { Enabled = true, RuleSetId = Contracts.Config.RuleSetPresets.EmptyUserAgent.Id },
                },
                Plain(App, "app.test") with
                {
                    ProofOfWork = new HostProofOfWorkSpec { Enabled = true, Difficulty = 18 },
                    RateLimit = new HostRateLimitSpec { Enabled = true, PolicyId = Standard },
                    Robots = new HostRobotsSpec { Mode = RobotsMode.BlockAiCrawlers },
                    AccessList = new HostAccessListSpec { Enabled = true, AccessListId = Office },
                    RuleSet = new HostRuleSetSpec { Enabled = true, RuleSetId = Scanners },
                    AuthProvider = new HostAuthProviderSpec { Enabled = true, AuthProviderId = Sso },
                    Routes =
                    [
                        Route("/") with { AccessList = Office, RuleSet = Scanners, AuthProvider = Sso, RateLimit = Standard, ProofOfWork = new HostProofOfWorkSpec { Enabled = true, Difficulty = 18 } },
                        Route("/api") with
                        {
                            AccessList = Lan,
                            RateLimit = Strict,
                            RateLimitRouteOverride = true,
                            ProofOfWork = new HostProofOfWorkSpec { Enabled = false, Difficulty = 18 },
                            ProofOfWorkRouteOverride = true,
                        },
                    ],
                },
            ],
        };
    }
}
