using Bunit;

using Paduraksa.Contracts.Metrics;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Components;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Metrics;
using Paduraksa.Proxy.Net;
using Paduraksa.ProxyProtocol;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>The security statistics: the counter cards and the refusals chart over the range the host page picks, how they poll, and a range that can't be read.</summary>
public sealed class SecurityStatisticsTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly HostId App = HostId.New();
    private static readonly UpstreamId Upstream = UpstreamId.New();
    private static readonly AuthProviderId Sso = AuthProviderId.New();
    private static readonly string[] CookieReasons = ["malformed", "bad-tag", "expired", "too-easy"];

    private static ProxyConfigModel Model(ConnectionsSpec? connections = null, AccessListSpec? list = null) => new()
    {
        Upstreams = [new UpstreamSpec { Id = Upstream, Destinations = [new DestinationSpec(DestinationId.New(), "http://127.0.0.1:1")] }],
        AccessLists = list is null ? [] : [list],
        Defaults = connections is null ? new DefaultsSpec() : new DefaultsSpec { Connections = connections },
        Hosts = [new HostSpec { Id = App, Names = ["app.test"], Https = false, Routes = [new RouteSpec { Id = RouteId.New(), Upstream = Upstream }] }],
    };

    private static async Task<(DashboardTestContext Context, SecurityRig Rig, SteppedTime Time)> StartAsync(bool database = true)
    {
        SteppedTime time = new(Start);
        SecurityRig rig = new(time);
        if (database)
        {
            await rig.UseDatabaseAsync();
        }

        DashboardTestContext context = new();
        rig.Register(context.Services);
        ConfigPages.SignIn(context, Role.Viewer, time);
        rig.Publish(Model());
        return (context, rig, time);
    }

    private static IRenderedComponent<SecurityStatistics> Render(DashboardTestContext context, MetricsRange range, bool visible = true, bool scoped = false) =>
        context.Render<SecurityStatistics>(parameters => parameters
            .Add(p => p.Range, range)
            .Add(p => p.Visible, visible)
            .Add(p => p.HostScoped, scoped)
            .Add(p => p.PollInterval, TimeSpan.FromSeconds(2)));

    [Fact]
    public async Task TheHoursCardsCountWhatTheGateDidFromTheSamplersMemory()
    {
        var (context, rig, _) = await StartAsync(database: false);
        await using var _1 = context;
        await using var _2 = rig;
        var pow = rig.ProofOfWork.Stats;
        pow.CountChallengeServed();
        pow.CountChallengeServed();
        pow.CountChallengeServed();
        pow.CountChallengeServed();
        pow.CountSolved();
        pow.CountVerifyRejected();
        pow.CountBlockedNonBrowser();
        pow.CountBlockedNonBrowser();
        pow.CountMalformed();
        pow.CountBadTag();
        pow.CountExpired();
        pow.CountTooEasy();
        rig.RateLimits.Stats.CountAllowed();
        rig.RateLimits.Stats.CountAllowed();
        rig.RateLimits.Stats.CountAllowed();
        rig.RateLimits.Stats.CountLimited();
        var lists = rig.AccessLists.Stats;
        lists.CountAddressRefused();
        lists.CountUnauthorized();
        lists.CountUnauthorized();
        lists.CountVerification();
        lists.CountQueued();
        lists.CountQueueFull();
        lists.CountQueueTimeout();
        var sso = rig.ForwardAuth.CountersFor(Sso);
        sso.CountAllowed();
        sso.CountCacheHit();
        sso.CountDenied();
        sso.CountRedirected();
        sso.CountCall();
        sso.CountError();
        sso.CountTimeout();
        sso.CountBusy();
        rig.Sampler.Tick();

        var section = Render(context, MetricsRange.Hour);

        string Reason(string card, string reason) => section.Find($"[data-section={card}] [data-reason={reason}]").TextContent.Trim();
        Assert.Equal("4", Reason("proof-of-work", "served"));
        Assert.Equal("1 (25%)", Reason("proof-of-work", "solved"));
        Assert.Equal("1", Reason("proof-of-work", "verify"));
        Assert.Equal("2", Reason("proof-of-work", "non-browser"));
        Assert.Equal(["1", "1", "1", "1"], CookieReasons.Select(reason => Reason("proof-of-work", reason)));
        Assert.Equal("4", Reason("rate-limits", "checked"));
        Assert.Equal("1 (25%)", Reason("rate-limits", "limited"));
        Assert.Equal("1", Reason("access-lists", "address-refused"));
        Assert.Equal("2", Reason("access-lists", "unauthorized"));
        Assert.Equal("1", Reason("access-lists", "checked"));
        Assert.Equal("2", Reason("access-lists", "busy"));
        Assert.Equal("1", Reason("sign-in", "sign-in-allowed"));
        Assert.Equal("2", Reason("sign-in", "sign-in-refused"));
        Assert.Equal("1", Reason("sign-in", "sign-in-cache-hits"));
        Assert.Equal("1", Reason("sign-in", "sign-in-calls"));
        Assert.Equal("3", Reason("sign-in", "sign-in-errors"));
        Assert.Empty(section.FindAll("[data-section=not-served]"));
        Assert.Empty(section.FindAll("[data-part=scope]"));
    }

    [Fact]
    public async Task TheConnectionsCardHasTheLiveLimitAndDenyListAndWhatWasRefusedBeforeTls()
    {
        var (context, rig, _) = await StartAsync(database: false);
        await using var _1 = context;
        await using var _2 = rig;
        var scanners = new AccessListSpec { Id = AccessListId.New(), Name = "scanners", Realm = "scanners", Deny = ["203.0.113.0/24"] };
        rig.Publish(Model(new ConnectionsSpec { MaxPerIp = 64, DenyList = scanners.Id }, scanners));
        var snapshot = rig.Store.Current;
        _ = rig.Connections.Admit(snapshot, IpKey.Parse("203.0.113.9"), ConnectionTransport.Tcp, out _);
        _ = rig.Connections.Admit(snapshot, IpKey.Parse("203.0.113.10"), ConnectionTransport.Quic, out _);
        Assert.Equal(ConnectionDecision.Counted, rig.Connections.Admit(snapshot, IpKey.Parse("192.0.2.1"), ConnectionTransport.Tcp, out _));
        rig.Sampler.Tick();

        var card = Render(context, MetricsRange.Hour).Find("[data-section=connections]");

        Assert.Equal("64", card.QuerySelector("[data-reason=connection-limit]")!.TextContent);
        Assert.EndsWith(DashboardRoutes.AccessList(scanners.Id), card.QuerySelector("[data-reason=connection-deny-list] a")!.GetAttribute("href"), StringComparison.Ordinal);
        Assert.Equal("2", card.QuerySelector("[data-reason=connection-denied]")!.TextContent);
        Assert.Equal("0", card.QuerySelector("[data-reason=connection-over-limit]")!.TextContent);
        Assert.Equal("1", card.QuerySelector("[data-reason=connection-counted]")!.TextContent);
        Assert.Null(card.QuerySelector("[data-reason=connection-uncounted]")); // only once the table has been full
    }

    [Fact]
    public async Task TheChartStacksTheReasonsThatRefusedSomethingAndTheGateAnswersAreACardOfTheirOwn()
    {
        var (context, rig, time) = await StartAsync(database: false);
        await using var _1 = context;
        await using var _2 = rig;
        rig.Sampler.Tick();
        rig.Refuse(App, GateOutcome.RefusedRateLimit, 30);
        rig.Refuse(App, GateOutcome.RefusedRuleSet, 10);
        rig.Refuse(App, GateOutcome.UnknownHost, 4);
        for (var second = 0; second < 25; second++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            rig.Sampler.Tick();
        }

        var section = Render(context, MetricsRange.Hour);

        Assert.Equal("40 refused", section.Find("[data-part=refused-total]").TextContent);
        var chart = section.Find("[data-chart=refusals]");
        Assert.Equal(["Rate limit", "Rules"], chart.QuerySelectorAll(".pdk-chart-key").Select(key => key.TextContent.Trim()));
        Assert.NotEmpty(chart.QuerySelectorAll("path.pdk-chart-band.pdk-chart-r1"));
        Assert.NotEmpty(chart.QuerySelectorAll("path.pdk-chart-band.pdk-chart-r3"));
        Assert.Equal("4", section.Find("[data-section=not-served] [data-reason=unknown-host]").TextContent);
    }

    [Fact]
    public async Task ALongerRangeReadsTheRollupsAndANewRangeStartsOver()
    {
        var (context, rig, time) = await StartAsync();
        await using var _1 = context;
        await using var _2 = rig;
        rig.RateLimits.Stats.CountLimited();
        rig.RateLimits.Stats.CountLimited();
        rig.Refuse(App, GateOutcome.RefusedRateLimit, 2);
        await rig.SettleAsync(time.Advance);

        var section = Render(context, MetricsRange.Week);

        Assert.Equal("2", section.Find("[data-section=rate-limits] [data-reason=limited]").TextContent.Trim()[..1]);
        Assert.Equal("2 refused", section.Find("[data-part=refused-total]").TextContent);

        section.Render(parameters => parameters.Add(p => p.Range, MetricsRange.Hour));
        Assert.Equal("2 refused", section.Find("[data-part=refused-total]").TextContent);
    }

    [Fact]
    public async Task TheHourPollsAtTheIntervalAndAHiddenTabWaitsUntilItIsShown()
    {
        var (context, rig, time) = await StartAsync(database: false);
        await using var _1 = context;
        await using var _2 = rig;
        rig.Sampler.Tick();
        var section = Render(context, MetricsRange.Hour);
        Assert.Equal("0", section.Find("[data-reason=served]").TextContent);

        rig.ProofOfWork.Stats.CountChallengeServed();
        rig.Sampler.Tick();
        time.Advance(TimeSpan.FromSeconds(2));
        await section.WaitForStateAsync(() => section.Find("[data-reason=served]").TextContent == "1");

        section.Render(parameters => parameters.Add(p => p.Visible, false));
        rig.ProofOfWork.Stats.CountChallengeServed();
        rig.Sampler.Tick();
        time.Advance(TimeSpan.FromSeconds(10));
        await section.InvokeAsync(() => { }); // Whatever the timer queued on the renderer has run once this does.
        Assert.Equal("1", section.Find("[data-reason=served]").TextContent);

        section.Render(parameters => parameters.Add(p => p.Visible, true));
        Assert.Equal("2", section.Find("[data-reason=served]").TextContent);
    }

    [Fact]
    public async Task RollupsThatCantBeReadAreAnAlertAndAHostScopedPageSaysTheseAreEveryHosts()
    {
        var (context, rig, _) = await StartAsync(database: false);
        await using var _1 = context;
        await using var _2 = rig;

        var section = Render(context, MetricsRange.Day, scoped: true);

        Assert.Contains("couldn't be read", section.Find("[data-part=security-failed]").TextContent, StringComparison.Ordinal);
        Assert.Equal("All hosts: these are not counted per host", section.Find("[data-part=scope]").TextContent);
        Assert.Equal("security", section.Find("section").Id);
    }
}
