using Bunit;

using Paduraksa.Contracts.Metrics;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Components;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Metrics;
using Paduraksa.Proxy.Net;
using Paduraksa.ProxyProtocol;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>The security overview's strip of four live tiles: the last hour's refusals overall and by family, a sparkline each, and the link to the Security tab.</summary>
public sealed class SecurityStripTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly HostId App = HostId.New();
    private static readonly UpstreamId Upstream = UpstreamId.New();

    private static readonly string[] Names = ["refused", "proof-of-work", "rate-limited", "denied"];

    private static async Task<(DashboardTestContext Context, SecurityRig Rig, SteppedTime Time)> StartAsync()
    {
        SteppedTime time = new(Start);
        SecurityRig rig = new(time);
        DashboardTestContext context = new();
        rig.Register(context.Services);
        ConfigPages.SignIn(context, Role.Viewer, time);
        rig.Publish(new ProxyConfigModel
        {
            Upstreams = [new UpstreamSpec { Id = Upstream, Destinations = [new DestinationSpec(DestinationId.New(), "http://127.0.0.1:1")] }],
            Hosts = [new HostSpec { Id = App, Names = ["app.test"], Https = false, Routes = [new RouteSpec { Id = RouteId.New(), Upstream = Upstream }] }],
        });
        return (context, rig, time);
    }

    /// <summary>Counts are in the hour's chart once the step they fall in has closed.</summary>
    private static void CloseStep(SecurityRig rig, SteppedTime time)
    {
        rig.Sampler.Tick();
        for (var second = 0; second < 25; second++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            rig.Sampler.Tick();
        }
    }

    private static IRenderedComponent<SecurityStrip> Render(DashboardTestContext context, bool active = true, TimeSpan? poll = null) =>
        context.Render<SecurityStrip>(parameters => parameters.Add(p => p.Active, active).Add(p => p.PollInterval, poll ?? TimeSpan.FromHours(1)));

    [Fact]
    public async Task TheTilesCountTheLastHoursRefusalsOverallAndByFamily()
    {
        var (context, rig, time) = await StartAsync();
        await using var _1 = context;
        await using var _2 = rig;
        rig.Refuse(App, GateOutcome.RefusedRateLimit, 900);
        rig.Refuse(App, GateOutcome.RefusedAccessList, 200);
        rig.Refuse(App, GateOutcome.RefusedRuleSet, 50);
        rig.Refuse(App, GateOutcome.RefusedProofOfWork, 54);
        rig.Refuse(App, GateOutcome.NoRoute, 3);
        CloseStep(rig, time);

        var strip = Render(context);

        Assert.Equal(Names, strip.FindAll("[data-tile]").Select(tile => tile.GetAttribute("data-tile")));
        Assert.Equal("1,204", strip.Find("[data-tile=refused] [data-part=value]").TextContent);
        Assert.Equal("54", strip.Find("[data-tile=proof-of-work] [data-part=value]").TextContent);
        Assert.Equal("900", strip.Find("[data-tile=rate-limited] [data-part=value]").TextContent);
        // Access lists and rule sets together; what no policy refused (a missing route) is not a refusal.
        Assert.Equal("250", strip.Find("[data-tile=denied] [data-part=value]").TextContent);
        Assert.Empty(strip.FindAll("[data-pending]"));
    }

    [Fact]
    public async Task EveryTileLinksToTheSecurityTabOverTheLastHourWithATitleOfItsOwn()
    {
        var (context, rig, _) = await StartAsync();
        await using var _1 = context;
        await using var _2 = rig;
        rig.Sampler.Tick();

        var strip = Render(context);

        var links = strip.FindAll("[data-tile] a[data-part=link]");
        Assert.Equal(4, links.Count);
        Assert.All(links, link => Assert.Equal("/analytics?tab=security&range=1h", link.GetAttribute("href")));
        Assert.Equal(4, links.Select(link => link.GetAttribute("title")).Distinct().Count());
    }

    [Fact]
    public async Task BeforeTheFirstReadTheTilesAreTheSameTilesPending()
    {
        var (context, rig, _) = await StartAsync();
        await using var _1 = context;
        await using var _2 = rig;

        // Not active: the page has not checked the caller's permission yet, so nothing is read.
        var strip = Render(context, active: false);

        Assert.Equal(Names, strip.FindAll("[data-pending]").Select(tile => tile.GetAttribute("data-pending")));
        Assert.Empty(strip.FindAll("[data-tile]"));
        Assert.All(strip.FindAll("[data-pending]"), tile =>
        {
            Assert.Equal("true", tile.GetAttribute("aria-busy"));
            Assert.NotNull(tile.QuerySelector("a[data-part=link]"));
            Assert.NotEmpty(tile.QuerySelector("[data-part=detail]")!.TextContent);
        });

        // Active, it reads and the same four become the loaded tiles.
        strip.Render(parameters => parameters.Add(p => p.Active, true));
        Assert.Empty(strip.FindAll("[data-pending]"));
        Assert.Equal(Names, strip.FindAll("[data-tile]").Select(tile => tile.GetAttribute("data-tile")));
    }

    [Fact]
    public async Task EachTileHasASparklineOnceThereAreTwoStepsAndItsFamilysColour()
    {
        var (context, rig, time) = await StartAsync();
        await using var _1 = context;
        await using var _2 = rig;
        rig.Refuse(App, GateOutcome.RefusedRateLimit, 10);
        rig.Sampler.Tick();

        var strip = Render(context, poll: TimeSpan.FromMilliseconds(20));
        Assert.Empty(strip.FindAll("[data-chart]"));

        for (var second = 0; second < 25; second++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            rig.Sampler.Tick();
        }

        await strip.WaitForStateAsync(() => strip.FindAll("[data-chart]").Count == 4);
        Assert.Equal(Names, strip.FindAll("[data-chart]").Select(chart => chart.GetAttribute("data-chart")));
        Assert.All(strip.FindAll("[data-chart]"), chart => Assert.Contains("pdk-chart-compact", chart.ClassName, StringComparison.Ordinal));
        Assert.NotEmpty(strip.Find("[data-tile=rate-limited]").QuerySelectorAll("rect.pdk-chart-hit[data-time]"));
    }

    [Fact]
    public async Task AHiddenBrowserTabStopsTheReadsAndShowingItReadsAtOnce()
    {
        var (context, rig, time) = await StartAsync();
        await using var _1 = context;
        await using var _2 = rig;
        rig.Refuse(App, GateOutcome.RefusedRateLimit, 3);
        CloseStep(rig, time);
        var strip = Render(context, poll: TimeSpan.FromMilliseconds(20));
        Assert.Equal("3", strip.Find("[data-tile=refused] [data-part=value]").TextContent);

        await strip.InvokeAsync(() => strip.Instance.PageVisibilityChangedAsync(false));
        rig.Refuse(App, GateOutcome.RefusedRateLimit, 4);
        CloseStep(rig, time);
        await Task.Delay(150, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal("3", strip.Find("[data-tile=refused] [data-part=value]").TextContent);

        await strip.InvokeAsync(() => strip.Instance.PageVisibilityChangedAsync(true));

        Assert.Equal("7", strip.Find("[data-tile=refused] [data-part=value]").TextContent);
    }
}
