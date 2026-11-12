using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Contracts.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.NotificationsArea;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>The notification history page, a channel's row figures and its drawer's recent deliveries: what they show, what the filters ask for, and the retry.</summary>
public sealed class NotificationHistoryTests
{
    // Read when used, not when the class loads: a slow run must not age the fixtures.
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;
    private static readonly NotificationChannelId Ops = NotificationChannelId.New();
    private static readonly NotificationChannelId Mail = NotificationChannelId.New();

    private static NotificationDeliverySummary Delivery(
        NotificationDeliveryState state,
        NotificationChannelId? channel = null,
        string? label = "Ops hook",
        int attempts = 1,
        string? error = null,
        DateTimeOffset? next = null) => new(
            NotificationDeliveryId.New(),
            channel ?? Ops,
            state,
            attempts,
            label,
            label is null ? null : NotificationChannelKind.Webhook,
            state == NotificationDeliveryState.Pending ? next ?? Now.AddSeconds(30) : null,
            Now.AddMinutes(-1),
            state == NotificationDeliveryState.Sent ? Now.AddMinutes(-1) : null,
            error);

    private static NotificationHistoryEvent Event(string title, int minutesAgo, params NotificationDeliverySummary[] deliveries) => new(
        NotificationEventId.New(),
        NotificationEventKind.UpstreamDown,
        title,
        "body",
        Now.AddMinutes(-minutesAgo).AddSeconds(-5),
        Now.AddMinutes(-minutesAgo).AddSeconds(-5),
        deliveries,
        "ups_x",
        "/upstreams");

    private static (FakeNotificationHistory Fake, DashboardTestContext Context, IRenderedComponent<MudSnackbarProvider> Snackbars) Context(Role role)
    {
        DashboardTestContext context = new();
        FakeNotificationHistory fake = new();
        context.Services.AddSingleton<IDashboardNotificationHistory>(fake);
        _ = IdentityPages.SignIn(context, role);
        context.Render<MudPopoverProvider>();
        return (fake, context, context.Render<MudSnackbarProvider>());
    }

    private static async Task<IRenderedComponent<NotificationHistory>> OpenAsync(DashboardTestContext context, Action<ComponentParameterCollectionBuilder<NotificationHistory>>? parameters = null)
    {
        var page = context.Render<NotificationHistory>(parameters ?? (_ => { }));
        await page.WaitForStateAsync(() => page.FindAll("[data-section=events] .pdk-row-skeleton").Count == 0 && page.FindAll("[data-section=events]").Count == 1);
        return page;
    }

    [Fact]
    public async Task EachEventShowsItsKindSubjectAndTimeWithAChipPerChannelAndTheErrorInTheTooltip()
    {
        var (fake, context, _) = Context(Role.Viewer);
        await using (context)
        {
            fake.Channels.AddRange([Channel(Ops, "Ops hook"), Channel(Mail, "Mail")]);
            fake.Events.Add(Event("Upstream media is down", 5,
                Delivery(NotificationDeliveryState.Sent),
                Delivery(NotificationDeliveryState.Failed, Mail, "Mail", attempts: 5, error: "The server refused the credentials (HTTP 401)."),
                Delivery(NotificationDeliveryState.Pending, Ops, "Ntfy", attempts: 2, error: "The server answered HTTP 503; will retry.", next: new DateTimeOffset(2026, 11, 12, 14, 3, 0, TimeSpan.Zero)),
                Delivery(NotificationDeliveryState.Expired, Ops, "Gotify", attempts: 0, error: "Not sent: the event was too old when Paduraksa started."),
                Delivery(NotificationDeliveryState.Dropped, Ops, label: null)));
            fake.Events.Add(Event("Certificate a.example.com expires in 7 days", 90));

            var page = await OpenAsync(context);

            var rows = page.FindAll("[data-event]");
            Assert.Equal(2, rows.Count);
            var first = rows[0];
            Assert.Equal("Upstream media is down", first.QuerySelector(".pdk-nh-title")!.TextContent.Trim());
            Assert.Equal("Upstream down", first.QuerySelector(".pdk-badge")!.TextContent.Trim());
            Assert.Equal("ups_x", first.QuerySelector("[data-part=subject]")!.TextContent.Trim());
            Assert.Equal("5 min ago", first.QuerySelector(".pdk-nh-time")!.TextContent.Trim());

            var chips = first.QuerySelectorAll("[data-chip]").ToDictionary(c => c.GetAttribute("data-chip")!, c => c);
            Assert.Equal("Ops hook", chips["delivery-sent"].TextContent.Trim());
            Assert.Equal("Mail failed", chips["delivery-failed"].TextContent.Trim());
            Assert.Equal("The server refused the credentials (HTTP 401). (5 attempts)", chips["delivery-failed"].GetAttribute("title"));
            Assert.Equal("Ntfy retrying, next 14:03 UTC", chips["delivery-pending"].TextContent.Trim());
            Assert.Contains("HTTP 503", chips["delivery-pending"].GetAttribute("title"), StringComparison.Ordinal);
            Assert.Equal("Gotify expired", chips["delivery-expired"].TextContent.Trim());
            Assert.Equal("Deleted channel dropped", chips["delivery-dropped"].TextContent.Trim());

            // A viewer reads: nothing to send again.
            Assert.Empty(page.FindAll("[data-action=retry]"));
            Assert.Equal("No channel was subscribed", rows[1].QuerySelector("[data-part=no-channel]")!.TextContent.Trim());
        }
    }

    [Fact]
    public async Task TheFiltersAskForWhatTheyNameAndTheQueryOfAChannelsLinkIsApplied()
    {
        var (fake, context, _) = Context(Role.Viewer);
        await using (context)
        {
            fake.Channels.AddRange([Channel(Ops, "Ops hook"), Channel(Mail, "Mail")]);
            fake.Events.Add(Event("e", 1, Delivery(NotificationDeliveryState.Failed)));

            context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/settings/notifications/history?channel={Mail}&outcome=failed");
            var page = await OpenAsync(context);

            Assert.Equal(Mail, fake.Queries[^1].Channel);
            Assert.Equal(NotificationDeliveryState.Failed, fake.Queries[^1].Outcome);
            Assert.NotNull(fake.Queries[^1].From);
            Assert.Equal("true", page.Find("[data-field=outcome] [data-value=failed]").GetAttribute("aria-checked"));
            Assert.Single(page.FindAll("[data-action=clear-filters]:not(.pdk-hidden)"));

            await page.Find("[data-field=outcome] [data-value=any]").ClickAsync(new());
            await page.WaitForStateAsync(() => fake.Queries[^1].Outcome is null);
            await page.Find("[data-field=range] [data-value=all]").ClickAsync(new());
            await page.WaitForStateAsync(() => fake.Queries[^1].From is null);
            await page.Find("[data-field=outcome] [data-value=sent]").ClickAsync(new());
            await page.WaitForStateAsync(() => fake.Queries[^1].Outcome == NotificationDeliveryState.Sent);
            var kind = page.FindComponent<MudSelect<NotificationEventKind?>>();
            await page.InvokeAsync(() => kind.Instance.ValueChanged.InvokeAsync(NotificationEventKind.CertificateExpiring));
            await page.WaitForStateAsync(() => fake.Queries[^1].Kind == NotificationEventKind.CertificateExpiring);

            await page.Find("[data-action=clear-filters]").ClickAsync(new());
            await page.WaitForStateAsync(() => fake.Queries[^1] is { Channel: null, Kind: null, Outcome: null, From: not null });
        }
    }

    [Fact]
    public async Task AnAdminSendsAFailedOrExpiredDeliveryAgainAndTheListFollows()
    {
        var (fake, context, snackbars) = Context(Role.Admin);
        await using (context)
        {
            var failed = Delivery(NotificationDeliveryState.Failed, error: "Could not connect to the server.");
            var sent = Delivery(NotificationDeliveryState.Sent, Mail, "Mail");
            fake.Events.Add(Event("Upstream media is down", 3, failed, sent));
            var page = await OpenAsync(context);

            // Only the one that was given up has the action; the sent one does not.
            var buttons = page.FindAll("[data-action=retry]");
            Assert.Single(buttons);
            Assert.Equal("Send to Ops hook again", buttons[0].GetAttribute("aria-label"));
            Assert.Empty(page.FindAll("[data-action=retry-all]"));

            await buttons[0].ClickAsync(new());
            await page.WaitForStateAsync(() => page.FindAll("[data-chip=delivery-pending]").Count == 1);

            Assert.Equal([failed.Id], fake.Retried);
            Assert.Equal("Ops hook queued", page.Find("[data-chip=delivery-pending]").TextContent.Trim());
            Assert.Empty(page.FindAll("[data-action=retry]"));
            await snackbars.WaitForStateAsync(() => snackbars.Markup.Contains("Sending to Ops hook again.", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task AnEventWithSeveralRetryableDeliveriesOffersToSendThemAllAndARefusalIsSaid()
    {
        var (fake, context, snackbars) = Context(Role.Admin);
        await using (context)
        {
            var one = Delivery(NotificationDeliveryState.Failed);
            var two = Delivery(NotificationDeliveryState.Expired, Mail, "Mail");
            var notification = Event("both down", 2, one, two);
            fake.Events.Add(notification);
            fake.Events.Add(Event("alone", 1, Delivery(NotificationDeliveryState.Failed)));
            var page = await OpenAsync(context);

            await page.Find("[data-action=retry-all]").ClickAsync(new());
            await page.WaitForStateAsync(() => page.FindAll("[data-chip=delivery-pending]").Count == 2);
            Assert.Equal([notification.Id], fake.RetriedEvents);

            await page.WaitForStateAsync(() => page.FindAll("[data-action=retry]").Count == 1);
            fake.Refusal = new NotificationRetryResult(IssuanceChangeStatus.Invalid, 0, [new ConfigError("/channel", NotificationErrorCodes.ChannelGone, "the channel was deleted or is paused; resume or add it again first")]);
            await page.Find("[data-action=retry]").ClickAsync(new());

            await snackbars.WaitForStateAsync(() => snackbars.Markup.Contains("The channel was deleted or is paused", StringComparison.Ordinal));
            Assert.Single(page.FindAll("[data-chip=delivery-failed]"));
        }
    }

    [Fact]
    public async Task TheListFollowsADeliveryThatChangesByItselfAndOlderEventsLoadOnRequest()
    {
        var (fake, context, _) = Context(Role.Viewer);
        await using (context)
        {
            for (var i = 0; i < NotificationHistory.PageSize + 5; i++)
            {
                fake.Events.Add(Event($"event {i}", i + 1, Delivery(NotificationDeliveryState.Pending, attempts: 1)));
            }

            // Fast enough to keep the test short, slow enough that a loaded runner's dispatcher still gets to the waits between polls.
            var page = await OpenAsync(context, p => p.Add(x => x.PollInterval, TimeSpan.FromMilliseconds(150)));
            Assert.Equal(NotificationHistory.PageSize, page.FindAll("[data-event]").Count);

            // A retrying delivery succeeds meanwhile: the first page is read again on its own.
            var before = fake.Queries.Count;
            fake.Replace(0, e => e with { Deliveries = [Delivery(NotificationDeliveryState.Sent)] });
            await page.WaitForStateAsync(() => page.FindAll("[data-chip=delivery-sent]").Count == 1);
            Assert.True(fake.Queries.Count > before);

            await page.Find("[data-action=older]").ClickAsync(new());
            await page.WaitForStateAsync(() => page.FindAll("[data-event]").Count == NotificationHistory.PageSize + 5);
            Assert.Empty(page.FindAll("[data-action=older]"));

            // With older pages open, a change is announced, not applied under the reader's eyes.
            fake.Replace(1, e => e with { Deliveries = [Delivery(NotificationDeliveryState.Failed, error: "x")] });
            await page.WaitForStateAsync(() => page.FindAll("[data-action=refresh]").Count == 1);
            Assert.Equal(NotificationHistory.PageSize + 5, page.FindAll("[data-event]").Count);
        }
    }

    [Fact]
    public async Task AnEmptyHistoryAndAFailedReadSaySoAndAFilteredOneSaysHowToWidenIt()
    {
        var (fake, context, _) = Context(Role.Viewer);
        await using (context)
        {
            var page = await OpenAsync(context);
            Assert.Contains("Nothing sent yet", page.Markup, StringComparison.Ordinal);

            await page.Find("[data-field=outcome] [data-value=failed]").ClickAsync(new());
            await page.WaitForStateAsync(() => page.Markup.Contains("No notification matches", StringComparison.Ordinal));

            fake.Failing = true;
            await page.Find("[data-field=outcome] [data-value=sent]").ClickAsync(new());
            await page.WaitForStateAsync(() => page.FindAll("[data-alert=load-failed]").Count == 1);
            Assert.Empty(page.FindAll(".pdk-row-skeleton"));
        }
    }

    [Fact]
    public async Task AChannelRowShowsWhatIsQueuedWhatFailedAndTheRateAndTheDrawerListsItsRecentDeliveries()
    {
        var (fake, context, _) = Context(Role.Admin);
        await using (context)
        {
            var channel = Channel(Ops, "Ops hook") with { Delivery = new NotificationChannelFigures(2, 197, 3) };
            var figures = context.Render<ChannelDeliveryFigures>(p => p.Add(f => f.Channel, channel));

            Assert.Equal("2 queued", figures.Find("[data-chip=queued]").TextContent.Trim());
            Assert.Equal("3 failed", figures.Find("[data-chip=failed-count]").TextContent.Trim());
            Assert.Equal($"/settings/notifications/history?channel={Ops}&outcome=failed", figures.Find("[data-action=view-failed]").GetAttribute("href"));
            Assert.Equal("98.5% delivered, 7 d", figures.Find("[data-part=success-rate]").TextContent.Trim());

            var quiet = context.Render<ChannelDeliveryFigures>(p => p.Add(f => f.Channel, channel with { Delivery = new NotificationChannelFigures(0, 40, 0) }));
            Assert.Empty(quiet.FindAll("[data-chip]"));
            Assert.Equal("100% delivered, 7 d", quiet.Find("[data-part=success-rate]").TextContent.Trim());
            var none = context.Render<ChannelDeliveryFigures>(p => p.Add(f => f.Channel, channel with { Delivery = new NotificationChannelFigures(0, 0, 0) }));
            Assert.Empty(none.FindAll("[data-part=success-rate]"));

            fake.Events.Add(Event("Upstream media is down", 4, Delivery(NotificationDeliveryState.Sent)));
            fake.Events.Add(Event("Certificate soon", 70, Delivery(NotificationDeliveryState.Failed, error: "Could not connect to the server.", attempts: 5)));
            var recent = context.Render<NotificationRecent>(p => p.Add(r => r.Channel, Ops));
            await recent.WaitForStateAsync(() => recent.FindAll("[data-event]").Count == 2);

            Assert.Equal(["Upstream media is down", "Certificate soon"], recent.FindAll(".pdk-nh-recent-title").Select(t => t.TextContent.Trim()));
            Assert.Equal(["Sent", "Failed"], recent.FindAll("[data-chip]").Select(c => c.TextContent.Trim()));
            Assert.Equal($"/settings/notifications/history?channel={Ops}", recent.Find("[data-action=all-deliveries]").GetAttribute("href"));
        }
    }

    [Theory]
    [InlineData(197, 3, "98.5")]
    [InlineData(999, 1, "99.9")]
    [InlineData(1_000_000, 1, "99.9")]
    [InlineData(1, 1, "50")]
    [InlineData(0, 4, "0")]
    [InlineData(40, 0, "100")]
    public void TheRateIsRoundedDownAndNeverHundredWhileSomethingFailed(int sent, int failed, string expected) =>
        Assert.Equal(expected, ChannelDeliveryFigures.Rate(new NotificationChannelFigures(0, sent, failed)));

    [Fact]
    public void TheWordingOfADeliveryNamesTheChannelTheOutcomeAndWhenTheNextTryIs()
    {
        Assert.Equal("Sent", DeliveryDisplay.Label(Delivery(NotificationDeliveryState.Sent), withChannel: false));
        Assert.Equal("Retrying, next 14:03 UTC", DeliveryDisplay.Label(Delivery(NotificationDeliveryState.Pending, next: new DateTimeOffset(2026, 11, 12, 14, 3, 20, TimeSpan.Zero)), withChannel: false));
        Assert.Equal("Ops hook queued", DeliveryDisplay.Label(Delivery(NotificationDeliveryState.Pending, attempts: 0), withChannel: true));
        Assert.Equal("Deleted Webhook channel", DeliveryDisplay.Channel(Delivery(NotificationDeliveryState.Sent) with { ChannelLabel = null }));
        Assert.True(DeliveryDisplay.CanRetry(Delivery(NotificationDeliveryState.Failed)));
        Assert.False(DeliveryDisplay.CanRetry(Delivery(NotificationDeliveryState.Failed, label: null)));
        Assert.False(DeliveryDisplay.CanRetry(Delivery(NotificationDeliveryState.Dropped)));
        Assert.False(DeliveryDisplay.CanRetry(Delivery(NotificationDeliveryState.Sent)));
    }

    private static NotificationChannelSummary Channel(NotificationChannelId id, string label) => new(
        id, NotificationChannelKind.Webhook, label, true, [NotificationEventKind.UpstreamDown], [], true, false, Now, Now);
}

internal sealed class FakeNotificationHistory : IDashboardNotificationHistory
{
    // The page polls from its own timer while a test changes events: both go through this lock, or a change lands under a read.
    private readonly Lock _gate = new();

    public List<NotificationHistoryEvent> Events { get; } = [];

    /// <summary>Changes an event while the page may be polling.</summary>
    public void Replace(int index, Func<NotificationHistoryEvent, NotificationHistoryEvent> change)
    {
        lock (_gate)
        {
            Events[index] = change(Events[index]);
        }
    }

    public List<NotificationChannelSummary> Channels { get; } = [];

    public List<NotificationHistoryFilter> Queries { get; } = [];

    public List<NotificationDeliveryId> Retried { get; } = [];

    public List<NotificationEventId> RetriedEvents { get; } = [];

    public bool Failing { get; set; }

    /// <summary>What a retry answers instead of committing.</summary>
    public NotificationRetryResult? Refusal { get; set; }

    public Task<NotificationHistoryPage> QueryAsync(NotificationHistoryFilter filter, int limit, NotificationEventId? after, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Query(filter, limit, after);
        }
    }

    private Task<NotificationHistoryPage> Query(NotificationHistoryFilter filter, int limit, NotificationEventId? after)
    {
        Queries.Add(filter);
        if (Failing)
        {
            throw new InvalidOperationException("database is locked");
        }

        var matching = Events
            .Where(e => filter.Kind is null || e.Kind == filter.Kind)
            .Where(e => filter.Outcome is null || e.Deliveries.Any(d => d.State == filter.Outcome))
            .Where(e => filter.Channel is null || e.Deliveries.Any(d => d.ChannelId == filter.Channel))
            .ToList();
        var skip = after is { } cursor ? matching.FindIndex(e => e.Id == cursor) + 1 : 0;
        var page = matching.Skip(skip).Take(limit).ToList();
        return Task.FromResult(new NotificationHistoryPage(page, skip + page.Count < matching.Count ? page[^1].Id : null));
    }

    public Task<IReadOnlyList<NotificationHistoryEvent>> RecentAsync(NotificationChannelId channel, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NotificationHistoryEvent>>([.. Events.Take(limit)]);

    public Task<IReadOnlyList<NotificationChannelSummary>> ChannelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NotificationChannelSummary>>([.. Channels]);

    public Task<NotificationRetryResult?> RetryDeliveryAsync(NotificationDeliveryId id, CancellationToken cancellationToken = default)
    {
        if (Refusal is { } refusal)
        {
            return Task.FromResult<NotificationRetryResult?>(refusal);
        }

        Retried.Add(id);
        Reset(e => e.Deliveries.Any(d => d.Id == id), d => d.Id == id);
        return Task.FromResult<NotificationRetryResult?>(new NotificationRetryResult(IssuanceChangeStatus.Committed, 1));
    }

    public Task<NotificationRetryResult?> RetryEventAsync(NotificationEventId id, CancellationToken cancellationToken = default)
    {
        RetriedEvents.Add(id);
        var count = Events.Where(e => e.Id == id).Sum(e => e.Deliveries.Count(d => d.State is NotificationDeliveryState.Failed or NotificationDeliveryState.Expired));
        Reset(e => e.Id == id, d => d.State is NotificationDeliveryState.Failed or NotificationDeliveryState.Expired);
        return Task.FromResult<NotificationRetryResult?>(new NotificationRetryResult(IssuanceChangeStatus.Committed, count));
    }

    private void Reset(Func<NotificationHistoryEvent, bool> events, Func<NotificationDeliverySummary, bool> deliveries)
    {
        for (var i = 0; i < Events.Count; i++)
        {
            if (events(Events[i]))
            {
                Events[i] = Events[i] with { Deliveries = [.. Events[i].Deliveries.Select(d => deliveries(d) ? d with { State = NotificationDeliveryState.Pending, Attempts = 0, NextAttemptAt = DateTimeOffset.UtcNow } : d)] };
            }
        }
    }
}
