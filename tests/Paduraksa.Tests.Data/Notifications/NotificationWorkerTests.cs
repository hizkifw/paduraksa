using Microsoft.EntityFrameworkCore;
using Paduraksa.Application.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Data.Notifications;
using Paduraksa.Notifications;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Notifications;

/// <summary>A channel's worker on a real store: what it claims, the digest ID, the retry schedule kept in the row, and the wake signal against the poll.</summary>
public sealed class NotificationWorkerTests : OutboxTestBase
{
    private static NotificationDeliveryException Transient() => new("The server answered HTTP 503; will retry.", transient: true);

    [Fact]
    public async Task ADeliveryEndsAsSentWithItsAttemptAndTimeAndCarriesTheEventIdOfItsEvent()
    {
        var channel = await AddChannel();
        var dispatcher = Dispatcher();
        await dispatcher.StartAsync(Ct);

        dispatcher.Publish(Event());
        await AdvanceUntilAsync(() => Sender.Sent.Count >= 1, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        await UntilAsync(async () => (await Deliveries()).All(d => d.State == NotificationDeliveryState.Sent));

        var delivery = Assert.Single(await Deliveries());
        var storedEvent = Assert.Single(await Events());
        Assert.Equal(1, delivery.Attempts);
        Assert.NotNull(delivery.SentAt);
        Assert.NotNull(delivery.LastAttemptAt);
        Assert.Null(delivery.LastError);
        Assert.Null(delivery.DigestId);
        Assert.Equal(storedEvent.Id.ToString(), Assert.Single(Sender.Sent).Message.EventId);
        Assert.Equal(channel, delivery.ChannelId);
    }

    [Fact]
    public async Task TheCountersByChannelKindAndTheQueueDepthFollowTheDeliveries()
    {
        var channel = await AddChannel(NotificationChannelKind.Webhook, true, NotificationEventKind.UpstreamDown);
        Sender.Script(channel, Transient());
        NotificationStats stats = new();
        var dispatcher = Dispatcher(stats: stats);
        await dispatcher.StartAsync(Ct);
        long Read(string name, string kind) => NotificationMetrics.Counters(stats).Single(c => c.Name == name && c.LabelValue == kind).Read();

        // It goes at once, fails and waits for its retry: one delivery in the queue, nothing ended yet.
        dispatcher.Publish(Event());
        await SettledAsync(() => Sender.Sent.Count >= 1);
        await SettledAsync(() => stats.Pending == 1 && NotificationMetrics.Counters(stats).Single(c => c.Name == "paduraksa.notification.queue_depth").Read() == 1);
        Assert.Equal(0, Read("paduraksa.notification.sent", "webhook"));

        await AdvanceUntilAsync(() => Sender.Sent.Count >= 2, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        await SettledAsync(() => stats.Pending == 0);

        Assert.Equal(1, Read("paduraksa.notification.sent", "webhook"));
        Assert.Equal(0, Read("paduraksa.notification.sent", "ntfy"));
        Assert.Equal(0, Read("paduraksa.notification.failed", "webhook"));
        Assert.Equal(["ntfy", "gotify", "discord", "webhook", "email"], NotificationMetrics.Counters(stats).Where(c => c.Name == "paduraksa.notification.sent").Select(c => c.LabelValue));
        Assert.True(NotificationMetrics.Counters(stats).Single(c => c.Name == "paduraksa.notification.queue_depth").Gauge);
    }

    [Fact]
    public async Task TheFirstSendGoesAtOnceAndOnlyWhatArrivesInsideTheWindowAfterItWaitsAndGoesTogether()
    {
        var channel = await AddChannel();
        var dispatcher = Dispatcher();
        await dispatcher.StartAsync(Ct);

        // An isolated notification is not held back: nothing moves the clock, and it is out.
        dispatcher.Publish(Event(title: "Upstream a is down"));
        await SettledAsync(() => Sender.Sent.Count >= 1);
        Assert.Equal(T0, Sender.Sent[0].At);
        Assert.Equal("Upstream a is down", Sender.Sent[0].Message.Title);

        // A burst right behind it waits for the rest of the window and is one digest.
        for (var i = 0; i < 4; i++)
        {
            dispatcher.Publish(Event(title: $"Upstream b{i} is down"));
        }

        await SettledAsync(() => dispatcher.Routed >= 5);
        await AdvanceAsync(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(500));
        Assert.Single(Sender.Sent);
        await AdvanceUntilAsync(() => Sender.Sent.Count >= 2, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(10));
        Assert.Equal("4 notifications: upstreams down", Sender.Sent[1].Message.Title);
        Assert.InRange(Sender.Sent[1].At - T0, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6));

        // Quiet for longer than the window: the next one is at once again.
        await AdvanceAsync(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
        var before = Time.Now;
        dispatcher.Publish(Event(title: "Upstream c is down"));
        await SettledAsync(() => Sender.Sent.Count >= 3);
        Assert.Equal(before, Sender.Sent[2].At);
        Assert.Equal(channel, Sender.Sent[2].Channel);
    }

    private async Task DeliveryStateAsync(NotificationEventId eventId, NotificationDeliveryState state)
    {
        for (var i = 0; i < 300 && (await Deliveries()).Single(d => d.EventId == eventId).State != state; i++)
        {
            await Task.Delay(10, Ct);
        }
    }

    [Fact]
    public async Task EventsThatWaitedOutADatabaseOutageInTheBufferAreExpiredNotSentWhenItEnds()
    {
        var channel = await AddChannel();
        await using HoldableFactory flaky = new(Db.Factory);
        var dispatcher = Dispatcher(Options(maxAge: TimeSpan.FromHours(1)), flaky);
        await dispatcher.StartAsync(Ct);
        await SettledAsync(() => Time.ArmedTimers >= 1); // The upkeep timer is armed once the start's recovery has run.

        flaky.Broken = true;
        dispatcher.Publish(Event(title: "Upstream media is down"));
        await SettledAsync(() => flaky.Refusals >= 1);
        // Two hours pass with the database down; the event is still in the flusher's hands, with the time it was published.
        Time.Advance(TimeSpan.FromHours(2));
        flaky.Broken = false;
        dispatcher.Publish(Event(title: "Upstream files is down"));
        await SettledAsync(() => dispatcher.Routed >= 2);
        await AdvanceUntilAsync(() => dispatcher.Stats.Ended(NotificationChannelKind.Webhook, NotificationDeliveryState.Expired) >= 1, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));

        await SettledAsync(() => Sender.Sent.Count >= 1);
        Assert.Equal(["Upstream files is down"], Sender.Sent.Select(s => s.Message.Title));
        var events = (await Events()).ToDictionary(e => e.Title);
        // The row is marked sent just after the send; a loaded runner can read it in between.
        await DeliveryStateAsync(events["Upstream files is down"].Id, NotificationDeliveryState.Sent);
        Assert.Equal(NotificationDeliveryState.Expired, (await Deliveries()).Single(d => d.EventId == events["Upstream media is down"].Id).State);
        Assert.Equal(NotificationDeliveryState.Sent, (await Deliveries()).Single(d => d.EventId == events["Upstream files is down"].Id).State);
        Assert.Equal(channel, Sender.Sent[0].Channel);
    }

    [Fact]
    public async Task APendingRowWithNoWorkerBecauseRecoveryFailedIsPickedUpByTheNextUpkeepTick()
    {
        var channel = await AddChannel();
        _ = await LeftBehind(channel, TimeSpan.FromMinutes(1));
        await using HoldableFactory flaky = new(Db.Factory) { Broken = true };
        var dispatcher = Dispatcher(factory: flaky);

        // The start's recovery throws: nothing wakes the channel.
        await dispatcher.StartAsync(Ct);
        await SettledAsync(() => flaky.Refusals >= 1 && Time.ArmedTimers >= 1);
        await AdvanceAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
        Assert.Empty(Sender.Sent);

        // The database is back; the upkeep timer fires, finishes the recovery and wakes the channel.
        flaky.Broken = false;
        await AdvanceUntilAsync(() => Sender.Sent.Count >= 1, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(2));

        Assert.Single(Sender.Sent);
        await UntilAsync(async () => (await Deliveries()).All(d => d.State == NotificationDeliveryState.Sent));
    }

    [Fact]
    public async Task AFailureWritingDownADeliveredMessageIsNotASendFailureAndItIsNotSentAgain()
    {
        var channel = await AddChannel();
        NotificationFactoryThatFailsTheChannelStateWrite factory = new(Db);
        var dispatcher = Dispatcher(factory: factory);
        await dispatcher.StartAsync(Ct);

        dispatcher.Publish(Event());
        await SettledAsync(() => Sender.Sent.Count >= 1);
        await UntilAsync(async () => (await Deliveries()).All(d => d.State == NotificationDeliveryState.Sent));
        await AdvanceAsync(TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(5));

        // The channel's last-sent write threw something that is not a database error: the message was delivered, the row says so, nothing retried it.
        Assert.Single(Sender.Sent);
        Assert.True(factory.Throws > 0);
        var row = Assert.Single(await Deliveries());
        Assert.Equal((NotificationDeliveryState.Sent, 1, null), (row.State, row.Attempts, row.LastError));
        Assert.Equal(1, dispatcher.Stats.Ended(NotificationChannelKind.Webhook, NotificationDeliveryState.Sent));
        Assert.Equal(0, dispatcher.Stats.Ended(NotificationChannelKind.Webhook, NotificationDeliveryState.Failed));
        Assert.Equal(channel, Assert.Single(Sender.Sent).Channel);
    }

    [Fact]
    public async Task OnlyDueDeliveriesAreClaimedAndNothingIsSentTwice()
    {
        var channel = await AddChannel();
        _ = await LeftBehind(channel, TimeSpan.FromMinutes(1), title: "due now");
        var (_, later) = await LeftBehind(channel, TimeSpan.FromMinutes(1), title: "due in ten minutes", nextAttemptAt: T0.AddMinutes(10), attempts: 1);
        var dispatcher = Dispatcher();
        await dispatcher.StartAsync(Ct);

        // The one that is due waits behind nothing; the retry that is not due holds the channel, so neither goes yet.
        await SettledAsync(() => Time.ArmedTimers >= 1);
        await AdvanceAsync(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5));
        Assert.Empty(Sender.Sent);

        await AdvanceUntilAsync(() => Sender.Sent.Count >= 1, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(20));
        await UntilAsync(async () => (await Deliveries()).All(d => d.State == NotificationDeliveryState.Sent));

        // Both, once each, in event order, as one digest now that they are due together.
        var digest = Assert.Single(Sender.Sent);
        Assert.Equal("2 notifications: upstreams down", digest.Message.Title);
        Assert.Contains(later, (await Deliveries()).Select(d => d.Id));
    }

    [Fact]
    public async Task EventsOfADigestibleKindShareADigestIdThatTheMessageCarriesAndOthersGoAlone()
    {
        await AddChannel(NotificationChannelKind.Webhook, true, NotificationEventKind.UpstreamDown, NotificationEventKind.SignInNewAddress);
        var dispatcher = Dispatcher();
        await dispatcher.StartAsync(Ct);

        for (var i = 0; i < 4; i++)
        {
            dispatcher.Publish(Event(title: $"Upstream u{i} is down"));
        }

        dispatcher.Publish(Event(NotificationEventKind.SignInNewAddress, "ops signed in from a new address"));
        await SettledAsync(() => dispatcher.Routed >= 5);
        await AdvanceUntilAsync(() => Sender.Sent.Count >= 2, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        await UntilAsync(async () => (await Deliveries()).All(d => d.State == NotificationDeliveryState.Sent));

        var rows = await Deliveries();
        var events = (await Events()).ToDictionary(e => e.Id);
        var digestRows = rows.Where(d => events[d.EventId].Kind == NotificationEventKind.UpstreamDown).ToList();
        var digestId = Assert.Single(digestRows.Select(d => d.DigestId).Distinct());
        Assert.NotNull(digestId);
        Assert.Equal(4, digestRows.Count);
        Assert.Null(Assert.Single(rows, d => events[d.EventId].Kind == NotificationEventKind.SignInNewAddress).DigestId);

        var digest = Assert.Single(Sender.Sent, s => s.Message.Kind == NotificationEventKind.UpstreamDown).Message;
        Assert.Equal(digestId.Value.ToString(), digest.EventId);
        var single = Assert.Single(Sender.Sent, s => s.Message.Kind == NotificationEventKind.SignInNewAddress).Message;
        Assert.StartsWith("nev_", single.EventId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRetryScheduleIsKeptInTheRowSoARestartContinuesIt()
    {
        var channel = await AddChannel();
        Sender.AlwaysFail(channel, Transient());
        var first = Dispatcher();
        await first.StartAsync(Ct);

        first.Publish(Event());
        await AdvanceUntilAsync(() => Sender.Sent.Count >= 1, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        var row = await WaitDeliveryAsync(d => d.Attempts == 1);
        Assert.Equal(NotificationDeliveryState.Pending, row.State);
        Assert.Equal("The server answered HTTP 503; will retry.", row.LastError);
        Assert.Equal(row.LastAttemptAt!.Value.AddSeconds(15), row.NextAttemptAt);

        // The process stops with the retry waiting. A second one, on the same store and the same clock, does not send it before it is due...
        await first.StopAsync(Ct);
        var second = Dispatcher();
        await second.StartAsync(Ct);
        await AdvanceAsync(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
        Assert.Single(Sender.Sent);

        // ... and sends it when it is, with the second wait (a minute) coming from the stored attempt count.
        await AdvanceUntilAsync(() => Sender.Sent.Count >= 2, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        var again = await WaitDeliveryAsync(d => d.Attempts == 2);
        Assert.Equal(again.LastAttemptAt!.Value.AddMinutes(1), again.NextAttemptAt);
        Sender.Heal(channel);
        await AdvanceUntilAsync(() => Sender.Sent.Count >= 3, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(3));
        var sent = await WaitDeliveryAsync(d => d.State == NotificationDeliveryState.Sent);
        Assert.Equal(3, sent.Attempts);
        Assert.Null(sent.LastError);
        // Every resend is the same event: a receiver can drop the repeats.
        Assert.Single(Sender.Sent.Select(s => s.Message.EventId).Distinct());
    }

    [Fact]
    public async Task APermanentFailureIsFinalAtOnceAndAGivenUpOneStaysFailedWithItsError()
    {
        var refused = await AddChannel();
        var dead = await AddChannel();
        Sender.AlwaysFail(refused, new NotificationDeliveryException("The server refused the credentials (HTTP 401).", transient: false));
        Sender.AlwaysFail(dead, Transient());
        var dispatcher = Dispatcher();
        await dispatcher.StartAsync(Ct);

        dispatcher.Publish(Event());
        await AdvanceUntilAsync(() => Sender.Sent.Count(s => s.Channel == dead) >= 5, TimeSpan.FromSeconds(15), TimeSpan.FromHours(1));
        await UntilAsync(async () => (await Deliveries()).All(d => d.State == NotificationDeliveryState.Failed));

        var rows = await Deliveries();
        var permanent = Assert.Single(rows, d => d.ChannelId == refused);
        Assert.Equal(1, permanent.Attempts);
        Assert.Equal("The server refused the credentials (HTTP 401).", permanent.LastError);
        var exhausted = Assert.Single(rows, d => d.ChannelId == dead);
        Assert.Equal(5, exhausted.Attempts);
        Assert.Equal(1, Sender.Sent.Count(s => s.Channel == refused));
        Assert.Equal(2L, dispatcher.Totals.Failed);
        Assert.Equal(2, dispatcher.Stats.Ended(NotificationChannelKind.Webhook, NotificationDeliveryState.Failed));
    }

    [Fact]
    public async Task NewDeliveriesWaitBehindARetryAndGoOutWithItInOrder()
    {
        var channel = await AddChannel();
        Sender.Script(channel, Transient());
        var dispatcher = Dispatcher();
        await dispatcher.StartAsync(Ct);

        dispatcher.Publish(Event(title: "Upstream first is down"));
        await AdvanceUntilAsync(() => Sender.Sent.Count >= 1, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        dispatcher.Publish(Event(title: "Upstream second is down"));
        await SettledAsync(() => dispatcher.Routed >= 2);

        // The second is due, but the first is retrying in 15 s: it waits, and does not jump the queue. When the retry comes due they go out together.
        await AdvanceAsync(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
        Assert.Single(Sender.Sent);
        await AdvanceUntilAsync(() => Sender.Sent.Count >= 2, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));

        Assert.Equal(["Upstream first is down", "2 notifications: upstreams down"], Sender.Sent.Select(s => s.Message.Title));
    }

    [Fact]
    public async Task AWakeSendsAfterTheCoalesceWindowWhileARowNobodyWokeWaitsForThePoll()
    {
        var channel = await AddChannel(NotificationChannelKind.Webhook, true, NotificationEventKind.SignInNewAddress);
        var dispatcher = Dispatcher();
        await dispatcher.StartAsync(Ct);
        dispatcher.Publish(Event(NotificationEventKind.SignInNewAddress, "woken"));
        await AdvanceUntilAsync(() => Sender.Sent.Count >= 1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        Assert.InRange(Sender.Sent[0].At - T0, TimeSpan.Zero, TimeSpan.FromSeconds(10));

        // A row written behind the flusher's back (another writer) has no signal: it is found by the 30 s poll, not before.
        await UntilAsync(async () => (await Deliveries()).All(d => d.State == NotificationDeliveryState.Sent));
        await SettledAsync(() => dispatcher.Stats.Pending == 0 && Time.ArmedTimers >= 1);
        _ = await LeftBehind(channel, TimeSpan.Zero, NotificationEventKind.SignInNewAddress, "found by the poll", nextAttemptAt: Time.Now);
        var written = Time.Now;
        await AdvanceAsync(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1));
        Assert.Single(Sender.Sent);

        await AdvanceUntilAsync(() => Sender.Sent.Count >= 2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        Assert.Equal("found by the poll", Sender.Sent[1].Message.Title);
        Assert.InRange(Sender.Sent[1].At - written, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(45));
    }

    [Fact]
    public async Task APausedChannelsPendingDeliveriesAreDroppedWithTheReasonAndNothingIsSent()
    {
        var channel = await AddChannel();
        _ = await LeftBehind(channel, TimeSpan.FromMinutes(1));
        await using (var context = Db.NewContext())
        {
            var row = await context.NotificationChannels.FindAsync([channel], Ct);
            row!.Enabled = false;
            await context.SaveChangesAsync(Ct);
        }

        var dispatcher = Dispatcher();
        await dispatcher.StartAsync(Ct);
        await AdvanceUntilAsync(() => dispatcher.Stats.Ended(NotificationChannelKind.Webhook, NotificationDeliveryState.Dropped) >= 1, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));

        var dropped = Assert.Single(await Deliveries());
        Assert.Equal(NotificationDeliveryState.Dropped, dropped.State);
        Assert.Equal("The channel is paused.", dropped.LastError);
        Assert.Empty(Sender.Sent);
        Assert.Equal(1, dispatcher.Stats.Ended(NotificationChannelKind.Webhook, NotificationDeliveryState.Dropped));
        Assert.Equal(0, dispatcher.Stats.Pending);
    }

    [Fact]
    public async Task ADeletedChannelsPendingDeliveriesAreDroppedAndKeepTheirHistory()
    {
        var channel = await AddChannel();
        var dispatcher = Dispatcher();
        await dispatcher.StartAsync(Ct);
        // The first goes at once; the second waits inside the window, and its channel is deleted meanwhile.
        dispatcher.Publish(Event(title: "first"));
        await SettledAsync(() => Sender.Sent.Count >= 1);
        dispatcher.Publish(Event(title: "second"));
        await SettledAsync(() => dispatcher.Routed >= 2);
        await using (var context = Db.NewContext())
        {
            _ = await context.NotificationChannels.Where(c => c.Id == channel).ExecuteDeleteAsync(Ct);
        }

        await AdvanceUntilAsync(() => dispatcher.QueueCount == 0, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        await UntilAsync(async () => (await Deliveries()).All(d => d.State != NotificationDeliveryState.Pending));

        var events = (await Events()).ToDictionary(e => e.Title);
        var rows = (await Deliveries()).ToDictionary(d => d.EventId);
        Assert.Equal(NotificationDeliveryState.Sent, rows[events["first"].Id].State);
        Assert.Equal((NotificationDeliveryState.Dropped, "The channel was deleted."), (rows[events["second"].Id].State, rows[events["second"].Id].LastError));
        Assert.Equal(2, (await Events()).Count);
        Assert.Equal(["first"], Sender.Sent.Select(s => s.Message.Title));
    }

    private async Task<NotificationDeliveryRow> WaitDeliveryAsync(Func<NotificationDeliveryRow, bool> match)
    {
        NotificationDeliveryRow? found = null;
        await UntilAsync(async () => (found = (await Deliveries()).FirstOrDefault(match)) is not null);
        return found!;
    }

    private sealed class HoldableFactory(IDbContextFactory<ConfigDbContext> inner) : IDbContextFactory<ConfigDbContext>, IAsyncDisposable
    {
        private int _refusals;

        public volatile bool Broken;

        /// <summary>How many times it refused while <see cref="Broken"/>: the signal that the code under test has tried and failed.</summary>
        public int Refusals => Volatile.Read(ref _refusals);

        public ConfigDbContext CreateDbContext() => inner.CreateDbContext();

        public Task<ConfigDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            if (Broken)
            {
                _ = Interlocked.Increment(ref _refusals);
                throw new Microsoft.Data.Sqlite.SqliteException("database is locked", 5);
            }

            return inner.CreateDbContextAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Contexts whose write to a channel's last-sent state throws an exception that is not a database error.</summary>
    private sealed class NotificationFactoryThatFailsTheChannelStateWrite(Config.TestConfigDatabase db) : IDbContextFactory<ConfigDbContext>
    {
        private readonly Interceptor _interceptor = new();

        public int Throws => _interceptor.Throws;

        public ConfigDbContext CreateDbContext() => db.NewContext(_interceptor);

        private sealed class Interceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
        {
            private int _throws;

            public int Throws => Volatile.Read(ref _throws);

            public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
                System.Data.Common.DbCommand command,
                Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
                Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
                CancellationToken cancellationToken = default)
            {
                if (command.CommandText.StartsWith("UPDATE \"NotificationChannels\"", StringComparison.Ordinal))
                {
                    _ = Interlocked.Increment(ref _throws);
                    throw new InvalidOperationException("not a database error");
                }

                return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
            }
        }
    }
}
