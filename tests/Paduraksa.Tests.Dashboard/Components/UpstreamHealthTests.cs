using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Paduraksa.Application.Notifications;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Components;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>Live upstream health on the Upstreams page, in an upstream's panel, on the hosts list and on the overview.</summary>
public sealed class UpstreamHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 14, 10, 0, TimeSpan.Zero);
    private static readonly DestinationId D1 = DestinationId.New();
    private static readonly DestinationId D2 = DestinationId.New();
    private static readonly UpstreamConfig Media = new(UpstreamId.New(), "media", Version: 1, Destinations: [new(D1, "http://10.0.0.5:8096"), new(D2, "http://10.0.0.6:8096")], ActiveHealth: new ActiveHealthConfig());
    private static readonly UpstreamConfig Plain = new(UpstreamId.New(), "plain", Version: 1, Destinations: [new(DestinationId.New(), "http://plain:80")]);
    private static readonly HostConfig MediaHost = new(HostId.New(), ["media.example.com"], Version: 1, Routes: [new RouteConfig(RouteId.New(), Media.Id)]);
    private static readonly HostConfig PlainHost = new(HostId.New(), ["plain.example.com"], Version: 1, Routes: [new RouteConfig(RouteId.New(), Plain.Id)]);

    private static UpstreamHealthView Down() => new(
        Media.Id,
        UpstreamHealthState.Down,
        [
            new DestinationHealthView(D1, "http://10.0.0.5:8096", DestinationHealthState.Unhealthy, "active probe: HTTP 503", Now.AddMinutes(-3)),
            new DestinationHealthView(D2, "http://10.0.0.6:8096", DestinationHealthState.Unhealthy, "active probe: timed out", Now.AddMinutes(-3)),
        ],
        new HealthChangeView(Now.AddMinutes(-3), "Down", null));

    private static UpstreamHealthView Degraded() => new(
        Media.Id,
        UpstreamHealthState.Degraded,
        [
            new DestinationHealthView(D1, "http://10.0.0.5:8096", DestinationHealthState.Unhealthy, "active probe: connection refused", Now.AddHours(-2), Flapping: true),
            new DestinationHealthView(D2, "http://10.0.0.6:8096", DestinationHealthState.Healthy, "active probe: HTTP 200"),
        ],
        new HealthChangeView(Now.AddHours(-2), "http://10.0.0.5:8096 down", "active probe: connection refused"));

    private static (DashboardTestContext Context, FakeDashboardConfig Config, UpstreamHealthBoard Board) Setup(Role role = Role.Viewer)
    {
        DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, role, new FixedClock(Now));
        config.Document = new ConfigDocument(Upstreams: [Media, Plain], Hosts: [MediaHost, PlainHost]);
        return (context, config, context.Services.GetRequiredService<UpstreamHealthBoard>());
    }

    [Fact]
    public async Task TheUpstreamsListShowsALiveDotPerUpstreamAndDestinationWithWhyAndTheLastChange()
    {
        var (context, _, board) = Setup();
        await using var _ = context;
        board.Update([Down()]);

        var page = context.Render<Upstreams>();

        await page.WaitForStateAsync(() => page.FindAll("[data-upstream=media] [data-health=down]").Count == 1);
        var row = page.Find("[data-upstream=media]");
        Assert.Contains("pdk-tone-error", row.QuerySelector("[data-health]")!.ClassName, StringComparison.Ordinal);
        Assert.Equal("Down", row.QuerySelector("[data-health]")!.GetAttribute("aria-label"));
        Assert.Equal(2, row.QuerySelectorAll("[data-destination-health=unhealthy]").Length);
        Assert.Contains("active probe: HTTP 503", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("active probe: timed out", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Last change: Down, 3 min ago", row.QuerySelector(".pdk-health-change")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUpstreamWithoutAHealthCheckGetsANeutralDotAndShowsNoHealth()
    {
        var (context, _, board) = Setup();
        await using var _ = context;
        board.Update([new UpstreamHealthView(Plain.Id, UpstreamHealthState.Unchecked, [new DestinationHealthView(Plain.Destinations[0].Id, "http://plain:80", DestinationHealthState.Unknown)])]);

        var page = context.Render<Upstreams>();

        await page.WaitForStateAsync(() => page.FindAll("[data-upstream=plain]").Count == 1);
        var row = page.Find("[data-upstream=plain]");
        Assert.Empty(row.QuerySelectorAll("[data-health]"));
        Assert.Empty(row.QuerySelectorAll("[data-destination-health]"));
        // Green would read as "checked and fine".
        Assert.Contains("pdk-tone-default", row.QuerySelector(".pdk-status-dot")!.ClassName, StringComparison.Ordinal);
        Assert.Equal("Not health-checked", row.QuerySelector(".pdk-status-dot")!.GetAttribute("aria-label"));
    }

    [Theory]
    [InlineData(200, "every 200 ms")]
    [InlineData(5000, "every 5 s")]
    public void AProbeIntervalUnderASecondIsShownInMilliseconds(int milliseconds, string expected)
    {
        var upstream = Plain with { ActiveHealth = new ActiveHealthConfig("/health", TimeSpan.FromMilliseconds(milliseconds)) };

        Assert.Contains(expected, Upstreams.Describe(upstream), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChangeOnTheBoardReachesThePageWithoutAReload()
    {
        var (context, _, board) = Setup();
        await using var _ = context;
        board.Update([Degraded()]);
        var seen = new List<IReadOnlyList<UpstreamHealthView>>();
        var feed = context.Render<UpstreamHealthFeed>(p => p
            .Add(f => f.PollInterval, TimeSpan.FromMilliseconds(20))
            .Add(f => f.Updated, views => seen.Add(views)));
        await UntilAsync(() => seen.Count == 1);

        // The same reading, replaced by the watcher every second, renders nothing; a different one is pushed once.
        board.Update([Degraded()]);
        // Absence window: six periods of the feed's 20 ms poll, in which an unchanged reading must push nothing.
        await Task.Delay(120, Xunit.TestContext.Current.CancellationToken);
        Assert.Single(seen);
        board.Update([Down()]);
        await UntilAsync(() => seen.Count == 2);

        Assert.Equal(UpstreamHealthState.Down, seen[1].Single().State);
        feed.Instance.Dispose();
    }

    [Fact]
    public async Task TheDrawerPanelNamesTheStateEachDestinationItsReasonAndFlapping()
    {
        var (context, _, board) = Setup();
        await using var _ = context;
        board.Update([Degraded()]);

        var panel = context.Render<UpstreamHealthPanel>(p => p.Add(x => x.Id, Media.Id));

        await panel.WaitForStateAsync(() => panel.FindAll("[data-section=health]").Count == 1);
        Assert.Equal("Degraded", panel.Find("[data-chip=upstream-health]").TextContent.Trim());
        Assert.Equal(2, panel.FindAll("[data-destination]").Count);
        var first = panel.Find("[data-destination='http://10.0.0.5:8096']").TextContent;
        Assert.Contains("active probe: connection refused", first, StringComparison.Ordinal);
        Assert.Contains("Flapping", first, StringComparison.Ordinal);
        Assert.Contains("Last change: http://10.0.0.5:8096 down, 2 h ago (active probe: connection refused)", panel.Find(".pdk-health-change").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDrawerPanelIsAbsentForAnUpstreamWithoutChecks()
    {
        var (context, _, board) = Setup();
        await using var _ = context;
        board.Update([new UpstreamHealthView(Plain.Id, UpstreamHealthState.Unchecked, [])]);

        var panel = context.Render<UpstreamHealthPanel>(p => p.Add(x => x.Id, Plain.Id));

        // Absence window for the panel's load: nothing signals a section that must not appear.
        await Task.Delay(50, Xunit.TestContext.Current.CancellationToken);
        Assert.Empty(panel.FindAll("[data-section=health]"));
    }

    [Theory]
    [InlineData(UpstreamHealthState.Down, "Upstream down")]
    [InlineData(UpstreamHealthState.Degraded, "Upstream degraded")]
    public async Task TheHostsListSaysWhereAHostsUpstreamIsHurt(UpstreamHealthState state, string chip)
    {
        var (context, _, board) = Setup();
        await using var _ = context;
        board.Update([(state == UpstreamHealthState.Down ? Down() : Degraded())]);

        var page = context.Render<Hosts>();

        await page.WaitForStateAsync(() => page.FindAll(".pdk-hosts-table").Count == 1);
        var hurt = page.Find("[data-host='media.example.com'] [data-chip=upstream-health]");
        Assert.Equal(chip, hurt.TextContent.Trim());
        Assert.Contains($"media is {state.ToString().ToLowerInvariant()}", hurt.GetAttribute("title"), StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-host='plain.example.com'] [data-chip=upstream-health]"));
    }

    [Fact]
    public async Task AHealthyOrSwitchedOffHostShowsNoUpstreamChip()
    {
        var (context, config, board) = Setup();
        await using var _ = context;
        config.Document = new ConfigDocument(Upstreams: [Media, Plain], Hosts: [MediaHost with { Enabled = false }, PlainHost]);
        board.Update([Down(), new UpstreamHealthView(Plain.Id, UpstreamHealthState.Healthy, [])]);

        var page = context.Render<Hosts>();

        await page.WaitForStateAsync(() => page.FindAll(".pdk-hosts-table").Count == 1);
        Assert.Empty(page.FindAll("[data-chip=upstream-health]"));
    }

    [Fact]
    public void TheWordsAreShortAndTheLastChangeSaysWhenAndWhy()
    {
        Assert.Equal("Last change: Down, yesterday", HealthDisplay.LastChange(new HealthChangeView(Now.AddHours(-30), "Down"), Now));
        Assert.Equal("Last change: Down, 10 Nov 2026", HealthDisplay.LastChange(new HealthChangeView(Now.AddDays(-2), "Down"), Now));
        Assert.Equal("Last change: Back up, 5 min ago", HealthDisplay.LastChange(new HealthChangeView(Now.AddMinutes(-5), "Back up"), Now));
        Assert.Equal("Checking", HealthDisplay.Word(UpstreamHealthState.Unknown));
        Assert.Equal("Not health-checked", HealthDisplay.Word(UpstreamHealthState.Unchecked));
    }

    /// <summary>The feed renders nothing, so there is no render to wait for: poll the condition.</summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            // Poll step of a wait bounded to two seconds.
            await Task.Delay(10, Xunit.TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
