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
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.RateLimiting;
using Paduraksa.Proxy.Snapshot;
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

    [Fact]
    public async Task TheTilesShowTheCountersAndTheirRatesFromTwoSamples()
    {
        await using DashboardTestContext context = new();
        var shell = Start(context, Role.Operator);
        shell.ProofOfWork.Stats.CountChallengeServed();
        shell.ProofOfWork.Stats.CountSolved();

        var page = context.Render<SecurityPage>(parameters => parameters.Add(p => p.PollInterval, TimeSpan.FromSeconds(2)));

        Assert.Equal("1", page.Find("[data-tile=served] [data-part=value]").TextContent);
        Assert.Equal(string.Empty, page.Find("[data-tile=served] [data-part=rate]").TextContent.Trim());

        for (var i = 0; i < 5; i++)
        {
            shell.ProofOfWork.Stats.CountChallengeServed();
        }

        shell.ProofOfWork.Stats.CountSolved();
        shell.ProofOfWork.Stats.CountVerifyRejected();
        shell.ProofOfWork.Stats.CountExpired();
        shell.RateLimits.Stats.CountAllowed();
        shell.RateLimits.Stats.CountAllowed();
        shell.RateLimits.Stats.CountAllowed();
        shell.RateLimits.Stats.CountLimited();
        shell.Time.Advance(TimeSpan.FromSeconds(2));
        await page.WaitForStateAsync(() => page.Find("[data-tile=served] [data-part=value]").TextContent == "6");

        Assert.Equal("2.5/s", page.Find("[data-tile=served] [data-part=rate]").TextContent);
        Assert.Equal("2", page.Find("[data-tile=solved] [data-part=value]").TextContent);
        Assert.Equal("0.5/s", page.Find("[data-tile=solved] [data-part=rate]").TextContent);
        Assert.Equal("33% of served", page.Find("[data-tile=solved] [data-part=detail]").TextContent);
        Assert.Equal("2", page.Find("[data-tile=rejected] [data-part=value]").TextContent);
        Assert.Equal("1", page.Find("[data-tile=rejected] [data-part=rate]").TextContent.Replace("/s", string.Empty, StringComparison.Ordinal));
        Assert.Equal("1", page.Find("[data-tile=limited] [data-part=value]").TextContent);
        Assert.Equal("25% of checked requests", page.Find("[data-tile=limited] [data-part=detail]").TextContent);
        Assert.Equal("0.5/s", page.Find("[data-tile=limited] [data-part=rate]").TextContent);
    }

    [Fact]
    public async Task RejectionsAreSummedInTheTileAndListedByReason()
    {
        await using DashboardTestContext context = new();
        var shell = Start(context, Role.Operator);
        var stats = shell.ProofOfWork.Stats;
        stats.CountVerifyRejected();
        stats.CountBlockedNonBrowser();
        stats.CountBlockedNonBrowser();
        stats.CountMalformed();
        stats.CountBadTag();
        stats.CountExpired();
        stats.CountTooEasy();

        var page = context.Render<SecurityPage>();

        Assert.Equal("7", page.Find("[data-tile=rejected] [data-part=value]").TextContent);
        Assert.Equal("1", page.Find("[data-reason=verify]").TextContent);
        Assert.Equal("2", page.Find("[data-reason=non-browser]").TextContent);
        Assert.Equal("1", page.Find("[data-reason=malformed]").TextContent);
        Assert.Equal("1", page.Find("[data-reason=bad-tag]").TextContent);
        Assert.Equal("1", page.Find("[data-reason=expired]").TextContent);
        Assert.Equal("1", page.Find("[data-reason=too-easy]").TextContent);
        Assert.Contains("Counted since Paduraksa started", page.Find("[data-part=since]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAccessListCountersAreListedWithRefusalsForBusyAddedUp()
    {
        await using DashboardTestContext context = new();
        var shell = Start(context, Role.Operator);
        var stats = shell.AccessLists.Stats;
        stats.CountAddressRefused();
        stats.CountUnauthorized();
        stats.CountUnauthorized();
        stats.CountVerification();
        stats.CountVerification();
        stats.CountVerification();
        stats.CountQueued();
        stats.CountQueueFull();
        stats.CountQueueFull();
        stats.CountQueueTimeout();

        var page = context.Render<SecurityPage>();

        Assert.Equal("1", page.Find("[data-section=access-lists] [data-reason=address-refused]").TextContent);
        Assert.Equal("2", page.Find("[data-section=access-lists] [data-reason=unauthorized]").TextContent);
        Assert.Equal("3", page.Find("[data-section=access-lists] [data-reason=checked]").TextContent);
        Assert.Equal("1", page.Find("[data-section=access-lists] [data-reason=queued]").TextContent);
        Assert.Equal("3", page.Find("[data-section=access-lists] [data-reason=busy]").TextContent);
    }

    [Fact]
    public async Task TheCloudflareColumnSaysLockdownAndOriginPullsAndCountsAHostWithOnlyThemAsProtected()
    {
        await using DashboardTestContext context = new();
        var shell = Start(context, Role.Operator);
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
        var shell = Start(context, Role.Operator);
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
    public async Task AHostWithNothingOnShowsMutedNoneInEveryCell()
    {
        await using DashboardTestContext context = new();
        var shell = Start(context, Role.Operator);
        shell.Publish(Hosts());

        var page = context.Render<SecurityPage>();

        var cells = page.Find("[data-host='files.test']").QuerySelectorAll(".pdk-dt-cell");
        Assert.Equal(5, cells.Length);
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
        var shell = Start(context, Role.Operator);
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
        Start(context, Role.Operator);

        var page = context.Render<SecurityPage>();

        Assert.Empty(page.FindAll("[data-host]"));
        Assert.Contains("No hosts yet", page.Find("[data-section=hosts]").TextContent, StringComparison.Ordinal);
        Assert.Equal(DashboardRoutes.NewHost, page.Find("[data-action=add-host]").GetAttribute("href"));
    }

    [Fact]
    public async Task AViewerWithNoHostsIsNotOfferedTheLink()
    {
        await using DashboardTestContext context = new();
        Start(context, Role.Viewer);

        var page = context.Render<SecurityPage>();

        Assert.Contains("No hosts yet", page.Markup, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-action=add-host]"));
    }

    [Fact]
    public async Task AHiddenTabStopsSamplingAndAShownOneStartsOverWithoutARate()
    {
        await using DashboardTestContext context = new();
        var shell = Start(context, Role.Operator);
        var page = context.Render<SecurityPage>();
        shell.ProofOfWork.Stats.CountChallengeServed();
        shell.Time.Advance(TimeSpan.FromSeconds(2));
        await page.WaitForStateAsync(() => page.Find("[data-tile=served] [data-part=value]").TextContent == "1");
        Assert.Equal("0.5/s", page.Find("[data-tile=served] [data-part=rate]").TextContent);

        await page.InvokeAsync(() => page.Instance.PageVisibilityChangedAsync(false));
        shell.ProofOfWork.Stats.CountChallengeServed();
        shell.Time.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(100, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal("1", page.Find("[data-tile=served] [data-part=value]").TextContent);

        await page.InvokeAsync(() => page.Instance.PageVisibilityChangedAsync(true));
        Assert.Equal("2", page.Find("[data-tile=served] [data-part=value]").TextContent);
        Assert.Equal(string.Empty, page.Find("[data-tile=served] [data-part=rate]").TextContent.Trim());
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

    [Theory]
    [InlineData(0, "0/s")]
    [InlineData(0.04, "<0.1/s")]
    [InlineData(0.4, "0.4/s")]
    [InlineData(2.5, "2.5/s")]
    [InlineData(9.96, "10/s")]
    [InlineData(12.4, "12/s")]
    public void RatesAreShownWithOneDecimalBelowTenAndWholeAboveIt(double rate, string expected) =>
        Assert.Equal(expected, SecurityFigures.RateText(rate));

    [Fact]
    public void ARateNeedsTimeToHavePassedAndNeverGoesNegative()
    {
        Assert.Null(SecurityFigures.Rate(5, 9, TimeSpan.Zero));
        Assert.Equal(0, SecurityFigures.Rate(9, 5, TimeSpan.FromSeconds(2)));
        Assert.Equal(2, SecurityFigures.Rate(5, 9, TimeSpan.FromSeconds(2)));
        Assert.Equal(string.Empty, SecurityFigures.RateText(null));
    }

    [Fact]
    public void ASolveRateOfNothingServedIsBlank()
    {
        Assert.Null(SecurityFigures.Share(0, 0));
        Assert.Equal("50%", SecurityFigures.Share(1, 2));
    }

    private static Shell Start(DashboardTestContext context, Role role)
    {
        Shell shell = new();
        context.Services.AddSingleton(new SecurityOverviewService(shell.Store, shell.ProofOfWork, shell.RateLimits, shell.Time, shell.AccessLists));
        ConfigPages.SignIn(context, role, shell.Time);
        return shell;
    }

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
                },
                Plain(App, "app.test") with
                {
                    ProofOfWork = new HostProofOfWorkSpec { Enabled = true, Difficulty = 18 },
                    RateLimit = new HostRateLimitSpec { Enabled = true, PolicyId = Standard },
                    Robots = new HostRobotsSpec { Mode = RobotsMode.BlockAiCrawlers },
                    AccessList = new HostAccessListSpec { Enabled = true, AccessListId = Office },
                    Routes =
                    [
                        Route("/") with { AccessList = Office, RateLimit = Standard, ProofOfWork = new HostProofOfWorkSpec { Enabled = true, Difficulty = 18 } },
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

    private sealed class Shell
    {
        public ManualTime Time { get; } = new(new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero));

        public SnapshotStore Store { get; } = new();

        public ProofOfWorkRuntime ProofOfWork { get; } = new();

        public RateLimitRuntime RateLimits { get; } = new(new RateLimitTable(64));

        public AccessListRuntime AccessLists { get; } = new(TimeProvider.System, new TestPasswords().Verifier);

        public void Publish(ProxyConfigModel model)
        {
            var result = SnapshotCompiler.Compile(model, new SnapshotCompilerOptions { RateLimits = RateLimits, ProofOfWork = ProofOfWork });
            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
            var snapshot = result.Snapshot!;
            Store.Publish(snapshot.Bind(1, [.. snapshot.RouteIdBySlot.Select(_ => new Endpoint(null, null, "route"))]));
        }
    }

    /// <summary>A clock that moves only when told, and fires the timers it passes: a <see cref="PeriodicTimer"/> ticks exactly when a test advances it.</summary>
    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                ManualTimer timer = new(callback, state, _now + dueTime, period);
                _timers.Add(timer);
                return timer;
            }
        }

        public void Advance(TimeSpan by)
        {
            List<ManualTimer> due = [];
            lock (_gate)
            {
                _now += by;
                foreach (var timer in _timers)
                {
                    while (timer.Due is { } at && at <= _now)
                    {
                        due.Add(timer);
                        timer.Next();
                    }
                }
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, DateTimeOffset due, TimeSpan period) : ITimer
        {
            private DateTimeOffset? _due = due;
            private bool _disposed;

            public DateTimeOffset? Due => _disposed ? null : _due;

            public void Next() => _due = period > TimeSpan.Zero && _due is { } at ? at + period : null;

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan newPeriod) => throw new NotSupportedException();

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                _disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }
}
