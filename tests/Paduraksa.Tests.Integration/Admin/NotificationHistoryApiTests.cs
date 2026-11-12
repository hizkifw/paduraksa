using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Paduraksa.Admin.Json;
using Paduraksa.Application.Notifications;
using Paduraksa.Contracts.Api;
using Paduraksa.Contracts.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Notifications;
using Paduraksa.Server.Hosting;
using static Paduraksa.Tests.Integration.Admin.AdminApiHarness;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// The notification history and the manual retry over the admin API on a real host with the real outbox: who may read and retry, the filters and the
/// paging, a failed delivery sent again (audited, with the same event ID), and the figures on the channel list.
/// </summary>
public sealed class NotificationHistoryApiTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AdminApiHarness _api = null!;
    private RecordingHook _hook = null!;

    private INotificationEvents Events => _api.Host.GetServices(HostRole.Admin).GetRequiredService<INotificationEvents>();

    public async ValueTask InitializeAsync()
    {
        // No coalescing wait: the tests are about the outbox, not about the window.
        _api = await AdminApiHarness.StartAsync(Ct, settings: new Dictionary<string, string?> { ["Paduraksa:Notifications:CoalesceWindow"] = "00:00:00" });
        _hook = await RecordingHook.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _hook.DisposeAsync();
        await _api.DisposeAsync();
    }

    private async Task<NotificationChannelSummary> AddChannelAsync(string label, params string[] events)
    {
        using var created = await _api.SendAsync(HttpMethod.Post, "/api/v1/notification-channels", content: JsonBody(
            $$"""{"kind":"webhook","label":"{{label}}","events":[{{string.Join(',', events.Select(e => $"\"{e}\""))}}],"secret":"{{_hook.Url}}/{{label}}"}"""));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return JsonSerializer.Deserialize(await created.Content.ReadAsStringAsync(Ct), AdminJsonContext.Default.NotificationChannelSummary)!;
    }

    private static NotificationEvent Down(string title) => new(
        NotificationEventKind.UpstreamDown, title, "body", NotificationSeverity.Critical, DateTimeOffset.UtcNow, "/upstreams", [new("upstreamId", "ups_x")], "ups_x");

    private async Task<Page<NotificationHistoryEvent>> HistoryAsync(string query = "", string? caller = "owner")
    {
        using var response = await _api.GetAsync("/api/v1/notifications/history" + query, caller);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(Ct), AdminJsonContext.Default.PageNotificationHistoryEvent)!;
    }

    private async Task<NotificationHistoryEvent> UntilAsync(string title, Func<NotificationHistoryEvent, bool> done)
    {
        for (var i = 0; i < 400; i++)
        {
            if ((await HistoryAsync("?limit=200")).Items.FirstOrDefault(e => e.Title == title) is { } found && done(found))
            {
                return found;
            }

            await Task.Delay(25, Ct);
        }

        throw new TimeoutException(title);
    }

    [Theory]
    [InlineData("owner", HttpStatusCode.OK)]
    [InlineData("admin", HttpStatusCode.OK)]
    [InlineData("operator", HttpStatusCode.OK)]
    [InlineData("viewer", HttpStatusCode.OK)]
    [InlineData("auditor", HttpStatusCode.OK)]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    public async Task ReadingTheHistoryNeedsOnlyProvidersRead(string? caller, HttpStatusCode expected)
    {
        using var response = await _api.GetAsync("/api/v1/notifications/history", caller);

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("owner", HttpStatusCode.NotFound)]
    [InlineData("admin", HttpStatusCode.NotFound)]
    [InlineData("operator", HttpStatusCode.Forbidden)]
    [InlineData("viewer", HttpStatusCode.Forbidden)]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    public async Task OnlyProvidersWriteMayRetryADeliveryOrAnEvent(string? caller, HttpStatusCode expected)
    {
        using var delivery = await _api.SendAsync(HttpMethod.Post, $"/api/v1/notifications/deliveries/{NotificationDeliveryId.New()}/retry", caller);
        using var notified = await _api.SendAsync(HttpMethod.Post, $"/api/v1/notifications/events/{NotificationEventId.New()}/retry", caller);

        Assert.Equal(expected, delivery.StatusCode);
        Assert.Equal(expected, notified.StatusCode);
    }

    [Fact]
    public async Task AFailedDeliveryIsSentAgainWithTheSameEventIdAndTheRetryIsAudited()
    {
        var channel = await AddChannelAsync("ops", "upstream.down");
        _hook.Status = 401;

        Events.Publish(Down("Upstream media is down"));
        var failed = await UntilAsync("Upstream media is down", e => e.Deliveries is [{ State: NotificationDeliveryState.Failed }]);

        var delivery = Assert.Single(failed.Deliveries);
        Assert.Equal((channel.Id, "ops", NotificationChannelKind.Webhook, 1), (delivery.ChannelId, delivery.ChannelLabel, delivery.ChannelKind, delivery.Attempts));
        Assert.Equal("The server refused the credentials (HTTP 401).", delivery.LastError);
        Assert.Null(delivery.NextAttemptAt);
        Assert.Equal(("ups_x", "/upstreams"), (failed.Subject, failed.LinkPath));

        _hook.Status = 200;
        using var retried = await _api.SendAsync(HttpMethod.Post, $"/api/v1/notifications/deliveries/{delivery.Id}/retry", "operator");
        Assert.Equal(HttpStatusCode.Forbidden, retried.StatusCode);
        using var again = await _api.SendAsync(HttpMethod.Post, $"/api/v1/notifications/deliveries/{delivery.Id}/retry", "admin");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, JsonSerializer.Deserialize(await again.Content.ReadAsStringAsync(Ct), AdminJsonContext.Default.NotificationRetryOutcome)!.Retried);

        var sent = await UntilAsync("Upstream media is down", e => e.Deliveries is [{ State: NotificationDeliveryState.Sent }]);
        Assert.NotNull(sent.Deliveries[0].SentAt);
        Assert.Equal(1, sent.Deliveries[0].Attempts);

        // Delivered twice (the first answered 401, the second 200), and a receiver can tell it is one event.
        Assert.Equal(2, _hook.Received.Count);
        Assert.All(_hook.Received, r => Assert.Equal(failed.Id.ToString(), r.EventId));
        Assert.All(_hook.Received, r => Assert.Contains($"\"eventId\":\"{failed.Id}\"", r.Body, StringComparison.Ordinal));

        using var audit = await _api.GetAsync("/api/v1/audit?action=notification.delivery.retry");
        var body = await audit.Content.ReadAsStringAsync(Ct);
        Assert.Contains(delivery.Id.ToString(), body, StringComparison.Ordinal);
        Assert.Contains(failed.Id.ToString(), body, StringComparison.Ordinal);
        Assert.Contains("\"actor\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetryingAnEventSendsEveryFailedDeliveryOfItAndNoOther()
    {
        _ = await AddChannelAsync("a", "upstream.down");
        _ = await AddChannelAsync("b", "upstream.down");
        _hook.Status = 401;
        Events.Publish(Down("both fail"));
        var failed = await UntilAsync("both fail", e => e.Deliveries.Count == 2 && e.Deliveries.All(d => d.State == NotificationDeliveryState.Failed));
        _hook.Status = 200;

        using var retried = await _api.SendAsync(HttpMethod.Post, $"/api/v1/notifications/events/{failed.Id}/retry", "admin");

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(2, JsonSerializer.Deserialize(await retried.Content.ReadAsStringAsync(Ct), AdminJsonContext.Default.NotificationRetryOutcome)!.Retried);
        _ = await UntilAsync("both fail", e => e.Deliveries.All(d => d.State == NotificationDeliveryState.Sent));
        using var audit = await _api.GetAsync("/api/v1/audit?action=notification.event.retry");
        Assert.Contains(failed.Id.ToString(), await audit.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyAFailedOrExpiredDeliveryWhoseChannelExistsCanBeSentAgain()
    {
        var channel = await AddChannelAsync("works", "upstream.down");
        Events.Publish(Down("sent fine"));
        var sent = await UntilAsync("sent fine", e => e.Deliveries is [{ State: NotificationDeliveryState.Sent }]);

        using var notRetryable = await _api.SendAsync(HttpMethod.Post, $"/api/v1/notifications/deliveries/{sent.Deliveries[0].Id}/retry");
        using var eventNotRetryable = await _api.SendAsync(HttpMethod.Post, $"/api/v1/notifications/events/{sent.Id}/retry");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notRetryable.StatusCode);
        Assert.Contains("notification.delivery.notRetryable", await notRetryable.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, eventNotRetryable.StatusCode);

        _hook.Status = 404;
        Events.Publish(Down("will fail"));
        var failed = await UntilAsync("will fail", e => e.Deliveries is [{ State: NotificationDeliveryState.Failed }]);
        using var gone = await _api.SendAsync(HttpMethod.Delete, $"/api/v1/notification-channels/{channel.Id}");
        Assert.Equal(HttpStatusCode.NoContent, gone.StatusCode);

        // The history stays, without the channel's name; a delivery to a channel that is gone cannot be sent.
        var after = await UntilAsync("will fail", e => e.Deliveries.Count == 1);
        Assert.Null(after.Deliveries[0].ChannelLabel);
        using var channelGone = await _api.SendAsync(HttpMethod.Post, $"/api/v1/notifications/deliveries/{failed.Deliveries[0].Id}/retry");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, channelGone.StatusCode);
        Assert.Contains("notification.delivery.channelGone", await channelGone.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEventNobodyIsSubscribedToIsListedWithNoDeliveriesAndTheFiltersNarrowTheList()
    {
        var one = await AddChannelAsync("one", "upstream.down");
        var two = await AddChannelAsync("two", "upstream.down", "certificate.expiring");
        Events.Publish(Down("down 1"));
        Events.Publish(new NotificationEvent(NotificationEventKind.CertificateExpiring, "cert soon", "b", NotificationSeverity.Warning, DateTimeOffset.UtcNow, "/certificates", null, "cert_x"));
        Events.Publish(new NotificationEvent(NotificationEventKind.SignInLockout, "locked", "b", NotificationSeverity.Warning, DateTimeOffset.UtcNow, "/users", null, null));
        await UntilAsync("down 1", e => e.Deliveries.Count == 2 && e.Deliveries.All(d => d.State == NotificationDeliveryState.Sent));
        await UntilAsync("cert soon", e => e.Deliveries.Count == 1 && e.Deliveries.All(d => d.State == NotificationDeliveryState.Sent));
        var nobody = await UntilAsync("locked", _ => true);
        Assert.Empty(nobody.Deliveries);

        // Newest first.
        Assert.Equal(["locked", "cert soon", "down 1"], (await HistoryAsync()).Items.Select(e => e.Title));
        Assert.Equal(["cert soon", "down 1"], (await HistoryAsync($"?channel={two.Id}")).Items.Select(e => e.Title));
        Assert.Equal(["down 1"], (await HistoryAsync($"?channel={one.Id}")).Items.Select(e => e.Title));
        Assert.Equal(["cert soon"], (await HistoryAsync("?kind=certificate.expiring")).Items.Select(e => e.Title));
        Assert.Equal(["cert soon", "down 1"], (await HistoryAsync("?outcome=sent")).Items.Select(e => e.Title));
        Assert.Empty((await HistoryAsync("?outcome=failed")).Items);
        Assert.Equal(["down 1"], (await HistoryAsync($"?channel={one.Id}&outcome=sent")).Items.Select(e => e.Title));
        Assert.Equal(["locked", "cert soon", "down 1"], (await HistoryAsync($"?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture))}")).Items.Select(e => e.Title));
        Assert.Empty((await HistoryAsync("?to=2020-01-01T00:00:00Z")).Items);
    }

    [Fact]
    public async Task ThePagesFollowTheCursorNewestFirstWithoutRepeatsAndBadParametersAreRefused()
    {
        _ = await AddChannelAsync("pages", "upstream.down");
        for (var i = 0; i < 5; i++)
        {
            Events.Publish(Down($"event {i}"));
            await UntilAsync($"event {i}", _ => true);
        }

        List<string> titles = [];
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await HistoryAsync("?limit=2" + (cursor is null ? string.Empty : $"&cursor={cursor}"));
            titles.AddRange(page.Items.Select(e => e.Title));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(["event 4", "event 3", "event 2", "event 1", "event 0"], titles);
        Assert.Equal(3, pages);

        foreach (var bad in new[] { "?limit=0", "?limit=500", "?cursor=nonsense", "?channel=host_x", "?kind=upstream.sideways", "?outcome=bogus", "?from=yesterday", "?from=2026-11-12T10:00:00Z&to=2026-11-12T09:00:00Z" })
        {
            using var response = await _api.GetAsync("/api/v1/notifications/history" + bad);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{bad}: {response.StatusCode}");
        }
    }

    [Fact]
    public async Task TheChannelListShowsWhatIsQueuedAndTheLastSevenDaysOutcomes()
    {
        var channel = await AddChannelAsync("figures", "upstream.down");
        Events.Publish(Down("fine 1"));
        Events.Publish(Down("fine 2"));
        await UntilAsync("fine 1", e => e.Deliveries.All(d => d.State == NotificationDeliveryState.Sent));
        await UntilAsync("fine 2", e => e.Deliveries.All(d => d.State == NotificationDeliveryState.Sent));
        _hook.Status = 403;
        Events.Publish(Down("broken"));
        await UntilAsync("broken", e => e.Deliveries.All(d => d.State == NotificationDeliveryState.Failed));

        using var list = await _api.GetAsync("/api/v1/notification-channels");
        var channels = JsonSerializer.Deserialize(await list.Content.ReadAsStringAsync(Ct), AdminJsonContext.Default.IReadOnlyListNotificationChannelSummary)!;

        Assert.Equal(new NotificationChannelFigures(0, 2, 1), channels.Single(c => c.Id == channel.Id).Delivery);
    }
}
