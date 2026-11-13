using Paduraksa.Application.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Notifications;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Notifications;

/// <summary>What a start does with what the last process left in the outbox: resume, expire the stale, and send again what a crash left unconfirmed.</summary>
public sealed class NotificationRestartTests : OutboxTestBase
{
    [Fact]
    public async Task PendingDeliveriesResumeAtTheNextStartAndAreSentOnce()
    {
        var channel = await AddChannel();
        var (eventId, _) = await LeftBehind(channel, TimeSpan.FromMinutes(5), title: "Upstream media is down");
        var dispatcher = Dispatcher();

        await dispatcher.StartAsync(Ct);
        await AdvanceUntilAsync(() => Sender.Sent.Count >= 1, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        await UntilAsync(async () => (await Deliveries()).All(d => d.State == NotificationDeliveryState.Sent));
        await AdvanceAsync(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(10));

        var sent = Assert.Single(Sender.Sent);
        Assert.Equal("Upstream media is down", sent.Message.Title);
        Assert.Equal(eventId.ToString(), sent.Message.EventId);
        Assert.Equal([("upstreamId", "ups_1")], sent.Message.Data!.Select(p => (p.Key, p.Value)));
        Assert.Equal("https://admin.example.com/upstreams", sent.Message.Link);
        Assert.Equal(0, dispatcher.Stats.Pending);
    }

    [Fact]
    public async Task DeliveriesForEventsOlderThanTheMaximumAgeExpireInsteadOfBeingSentAndFreshOnesStillGo()
    {
        var channel = await AddChannel();
        var other = await AddChannel(NotificationChannelKind.Ntfy, true, NotificationEventKind.UpstreamDown);
        var (staleEvent, stale) = await LeftBehind(channel, TimeSpan.FromHours(3), title: "stale");
        _ = await LeftBehind(other, TimeSpan.FromHours(2), title: "also stale");
        _ = await LeftBehind(channel, TimeSpan.FromMinutes(10), NotificationEventKind.SignInNewAddress, "fresh");
        var dispatcher = Dispatcher(Options(maxAge: TimeSpan.FromHours(1)));

        await dispatcher.StartAsync(Ct);
        await AdvanceUntilAsync(() => Sender.Sent.Count >= 1, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        await UntilAsync(async () => (await Deliveries()).All(d => d.State != NotificationDeliveryState.Pending));

        Assert.Equal(["fresh"], Sender.Sent.Select(s => s.Message.Title));
        var rows = await Deliveries();
        var expired = Assert.Single(rows, d => d.Id == stale);
        Assert.Equal(NotificationDeliveryState.Expired, expired.State);
        Assert.Equal(staleEvent, expired.EventId);
        Assert.Equal("Not sent: it was older than the maximum delivery age.", expired.LastError);
        Assert.Equal(2, rows.Count(d => d.State == NotificationDeliveryState.Expired));
        Assert.Equal(1, dispatcher.Stats.Ended(NotificationChannelKind.Webhook, NotificationDeliveryState.Expired));
        Assert.Equal(1, dispatcher.Stats.Ended(NotificationChannelKind.Ntfy, NotificationDeliveryState.Expired));
        // The history keeps what was not sent.
        Assert.Equal(3, (await Events()).Count);
    }

    [Fact]
    public async Task ASendThatCrashedBeforeItsRowWasUpdatedIsSentAgainWithTheSameEventId()
    {
        var channel = await AddChannel();
        var (eventId, _) = await LeftBehind(channel, TimeSpan.FromMinutes(1));
        var first = Dispatcher();

        // The process ends after the service accepted the message and before the row says so: from the database's side the send never happened.
        var stopped = StopInTheMiddleOfASend(first);
        await first.StartAsync(Ct);
        await AdvanceUntilAsync(() => Sender.Sent.Count >= 1, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        await (await stopped.Task.WaitAsync(Ct)).WaitAsync(Ct);

        Assert.Equal(NotificationDeliveryState.Pending, Assert.Single(await Deliveries()).State);

        Sender.OnSend(null);
        var second = Dispatcher();
        await second.StartAsync(Ct);
        await AdvanceUntilAsync(() => Sender.Sent.Count >= 2, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        await UntilAsync(async () => (await Deliveries()).All(d => d.State == NotificationDeliveryState.Sent));

        // At least once: twice, and a receiver can tell it is the same event.
        Assert.Equal(2, Sender.Sent.Count);
        Assert.All(Sender.Sent, s => Assert.Equal(eventId.ToString(), s.Message.EventId));
    }

    [Fact]
    public async Task ADigestResentAfterACrashKeepsItsIdAndItsMembers()
    {
        var channel = await AddChannel();
        for (var i = 0; i < 3; i++)
        {
            _ = await LeftBehind(channel, TimeSpan.FromMinutes(1), title: $"Upstream u{i} is down");
        }

        var first = Dispatcher();
        var stopped = StopInTheMiddleOfASend(first);
        await first.StartAsync(Ct);
        await AdvanceUntilAsync(() => Sender.Sent.Count >= 1, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        await (await stopped.Task.WaitAsync(Ct)).WaitAsync(Ct);
        var rows = await Deliveries();
        var digestId = Assert.Single(rows.Select(d => d.DigestId).Distinct());
        Assert.NotNull(digestId);

        // A new, unrelated event of the same kind arrives while the digest is unconfirmed: it is not folded into it.
        _ = await LeftBehind(channel, TimeSpan.Zero, title: "Upstream late is down");
        Sender.OnSend(null);
        var second = Dispatcher();
        await second.StartAsync(Ct);
        await AdvanceUntilAsync(() => Sender.Sent.Count >= 3, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));

        Assert.Equal(digestId.Value.ToString(), Sender.Sent[0].Message.EventId);
        Assert.Equal(digestId.Value.ToString(), Sender.Sent[1].Message.EventId);
        Assert.Equal(Sender.Sent[0].Message.Body, Sender.Sent[1].Message.Body);
        Assert.StartsWith("nev_", Sender.Sent[2].Message.EventId, StringComparison.Ordinal);
    }

    /// <summary>The next send stops <paramref name="dispatcher"/> once it is recorded and never answers: the process ends between the send and the row update.</summary>
    private TaskCompletionSource<Task> StopInTheMiddleOfASend(NotificationDispatcher dispatcher)
    {
        TaskCompletionSource<Task> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Sender.OnSend((message, token) =>
        {
            _ = stopped.TrySetResult(Task.Run(() => dispatcher.StopAsync(CancellationToken.None), CancellationToken.None));
            // A sender that never answers: the stop has to cut it off.
            return Task.Delay(Timeout.Infinite, token);
        });
        return stopped;
    }
}
