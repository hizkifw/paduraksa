using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Paduraksa.Application.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Data.Notifications;
using Paduraksa.Notifications;
using Paduraksa.Tests.Data.Config;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Notifications;

/// <summary>The dispatcher on a real database with a scripted sender and a manual clock: routing, digests, retry and backoff, give-up, bounds.</summary>
public sealed class NotificationDispatcherTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 14, 0, 0, TimeSpan.Zero);

    private readonly ManualTimeProvider _time = new(T0);
    private readonly ScriptedSender _sender;
    private TestConfigDatabase _db = null!;

    public NotificationDispatcherTests() => _sender = new ScriptedSender(_time);

    public async ValueTask InitializeAsync() => _db = await CreateAsync();

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    private NotificationDispatcher Dispatcher(NotificationOptions? options = null) =>
        new(_db.Factory, new Senders(_sender), options ?? new NotificationOptions { AdminUrl = new Uri("https://admin.example.com") }, _time, NullLogger<NotificationDispatcher>.Instance);

    private async Task<NotificationChannelId> AddChannel(NotificationChannelKind kind = NotificationChannelKind.Webhook, bool enabled = true, params NotificationEventKind[] events)
    {
        NotificationChannelRow row = new()
        {
            Id = NotificationChannelId.New(),
            Kind = kind,
            Label = "channel",
            Enabled = enabled,
            Events = [.. (events.Length == 0 ? [NotificationEventKind.UpstreamDown] : events).Select(e => e.ToName())],
            SecretId = SecretId.New(),
            CreatedAt = T0,
            UpdatedAt = T0,
        };
        await using var context = _db.NewContext();
        context.NotificationChannels.Add(row);
        await context.SaveChangesAsync(Ct);
        return row.Id;
    }

    private async Task<NotificationChannelRow> Row(NotificationChannelId id)
    {
        await using var context = _db.NewContext();
        return await context.NotificationChannels.AsNoTracking().SingleAsync(c => c.Id == id, Ct);
    }

    private static NotificationEvent Event(NotificationEventKind kind = NotificationEventKind.UpstreamDown, string title = "Upstream media is down") =>
        new(kind, title, "body", NotificationSeverity.Critical, T0, "/upstreams");

    /// <summary>Real time lets the loops run; the manual clock moves in <paramref name="step"/>s so timers they arm are reached in order.</summary>
    private async Task AdvanceAsync(TimeSpan total, TimeSpan? step = null)
    {
        var each = step ?? TimeSpan.FromSeconds(1);
        for (var spent = TimeSpan.Zero; spent < total; spent += each)
        {
            await Task.Delay(8, Ct);
            _time.Advance(each);
        }

        await Task.Delay(30, Ct);
    }

    // The fake clock moves only when a test advances it, but the dispatcher's loops run on real threads: after the clock has moved, a loaded
    // machine may not have run them yet, so what a test expects is waited for in real time rather than read at once.
    private static async Task SettledAsync(Func<bool> done) => await UntilAsync(() => done() ? new object() : null);

    private Task<NotificationChannelRow> RowSentAsync(NotificationChannelId id) => RowWhenAsync(id, static row => row.LastSentAt is not null);

    private async Task<NotificationChannelRow> RowWhenAsync(NotificationChannelId id, Func<NotificationChannelRow, bool> done)
    {
        for (var i = 0; i < 300; i++)
        {
            if (await Row(id) is var row && done(row))
            {
                return row;
            }

            await Task.Delay(10, Ct);
        }

        throw new TimeoutException();
    }

    // A retry's wait is armed only when the loop gets to it; if the clock moved first, that wait starts later in fake time. So a test that counts
    // retries moves the clock until they have happened (bounded), rather than by a fixed amount.
    private async Task AdvanceUntilAsync(Func<bool> done, TimeSpan step, TimeSpan max)
    {
        for (var spent = TimeSpan.Zero; !done() && spent < max; spent += step)
        {
            await Task.Delay(8, Ct);
            _time.Advance(step);
        }

        await SettledAsync(done);
    }

    private static async Task<T> UntilAsync<T>(Func<T?> read)
        where T : class
    {
        for (var i = 0; i < 300; i++)
        {
            if (read() is { } value)
            {
                return value;
            }

            await Task.Delay(10, Ct);
        }

        throw new TimeoutException();
    }

    [Fact]
    public async Task AnEventGoesToEveryEnabledChannelSubscribedToItAndToNoOtherWithTheDashboardLink()
    {
        var subscribed = await AddChannel(NotificationChannelKind.Webhook, true, NotificationEventKind.UpstreamDown);
        var also = await AddChannel(NotificationChannelKind.Ntfy, true, NotificationEventKind.UpstreamDown, NotificationEventKind.UpstreamUp);
        await AddChannel(NotificationChannelKind.Discord, true, NotificationEventKind.CertificateExpiring);
        await AddChannel(NotificationChannelKind.Gotify, false, NotificationEventKind.UpstreamDown);
        var dispatcher = Dispatcher();
        await dispatcher.StartAsync(Ct);

        dispatcher.Publish(Event());
        await AdvanceUntilAsync(() => _sender.Sent.Count >= 2, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));

        Assert.Equal([subscribed, also], _sender.Sent.Select(s => s.Channel).Order());
        Assert.All(_sender.Sent, s => Assert.Equal("https://admin.example.com/upstreams", s.Message.Link));
        await RowSentAsync(subscribed);
        await dispatcher.StopAsync(Ct);
    }

    [Fact]
    public async Task WithoutAKnownAdminUrlTheMessageHasNoLink()
    {
        await AddChannel();
        var dispatcher = Dispatcher(new NotificationOptions());
        await dispatcher.StartAsync(Ct);

        dispatcher.Publish(Event());
        await AdvanceUntilAsync(() => _sender.Sent.Count >= 1, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));

        Assert.Null(Assert.Single(_sender.Sent).Message.Link);
        await dispatcher.StopAsync(Ct);
    }

    [Fact]
    public async Task AStormOfOneKindBecomesOneDigestPerChannelAndOtherKindsStaySeparate()
    {
        await AddChannel(NotificationChannelKind.Webhook, true, NotificationEventKind.UpstreamDown, NotificationEventKind.SignInNewAddress);
        var dispatcher = Dispatcher();
        await dispatcher.StartAsync(Ct);

        for (var i = 0; i < 12; i++)
        {
            dispatcher.Publish(Event(title: $"Upstream u{i} is down"));
        }

        dispatcher.Publish(Event(NotificationEventKind.SignInNewAddress, "ops signed in from a new address"));
        dispatcher.Publish(Event(NotificationEventKind.SignInNewAddress, "dev signed in from a new address"));

        // Every event in its channel's queue before the clock moves, or a slow route lands one after the digest's window.
        await SettledAsync(() => dispatcher.Routed >= 14);
        await AdvanceUntilAsync(() => _sender.Sent.Count >= 3, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));

        var digest = Assert.Single(_sender.Sent, s => s.Message.Kind == NotificationEventKind.UpstreamDown);
        Assert.Equal("12 notifications: upstreams down", digest.Message.Title);
        Assert.Equal(11, digest.Message.Body.Split('\n').Length);
        Assert.Contains("- and 2 more", digest.Message.Body, StringComparison.Ordinal);
        Assert.Equal(2, _sender.Sent.Count(s => s.Message.Kind == NotificationEventKind.SignInNewAddress));
        await dispatcher.StopAsync(Ct);
    }

    [Fact]
    public async Task ATransientFailureIsRetriedWithGrowingWaitsAndASuccessClearsTheError()
    {
        var channel = await AddChannel();
        _sender.Script(channel, new NotificationDeliveryException("The server answered HTTP 503; will retry.", transient: true), new NotificationDeliveryException("The server answered HTTP 503; will retry.", transient: true));
        var dispatcher = Dispatcher();
        await dispatcher.StartAsync(Ct);

        dispatcher.Publish(Event());
        await AdvanceUntilAsync(() => _sender.Sent.Count >= 3, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10));

        Assert.Equal(3, _sender.Sent.Count);

        // The waits are 15 s and then 1 min; the manual clock moves a second per real tick, so each gap may run a few seconds long.
        var at = _sender.Sent.Select(s => s.At).ToList();
        Assert.InRange(at[1] - at[0], TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(25));
        Assert.InRange(at[2] - at[1], TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(70));
        var row = await RowSentAsync(channel);
        Assert.Null(row.LastError);
        Assert.Equal((1L, 0L), dispatcher.Totals);
        await dispatcher.StopAsync(Ct);
    }

    [Fact]
    public async Task AMessageIsGivenUpAfterTheLastRetryAndTheChannelShowsTheError()
    {
        var channel = await AddChannel();
        _sender.AlwaysFail(channel, new NotificationDeliveryException("Could not connect to the server.", transient: true));
        var dispatcher = Dispatcher();
        await dispatcher.StartAsync(Ct);

        dispatcher.Publish(Event());
        await AdvanceUntilAsync(() => _sender.Sent.Count >= 5, TimeSpan.FromSeconds(15), TimeSpan.FromHours(1));
        var row = await RowWhenAsync(channel, static row => row.LastError is not null);
        await AdvanceAsync(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(1));

        Assert.Equal(5, _sender.Sent.Count);
        Assert.Equal("Could not connect to the server.", row.LastError);
        Assert.NotNull(row.LastErrorAt);
        Assert.Null(row.LastSentAt);
        Assert.Equal((0L, 1L), dispatcher.Totals);
        await dispatcher.StopAsync(Ct);
    }

    [Fact]
    public async Task APermanentFailureIsNotRetried()
    {
        var channel = await AddChannel();
        _sender.AlwaysFail(channel, new NotificationDeliveryException("The server refused the credentials (HTTP 401).", transient: false));
        var dispatcher = Dispatcher();
        await dispatcher.StartAsync(Ct);

        dispatcher.Publish(Event());
        await AdvanceUntilAsync(() => _sender.Sent.Count >= 1, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(2));
        var row = await RowWhenAsync(channel, static row => row.LastError is not null);
        await AdvanceAsync(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(5));

        Assert.Single(_sender.Sent);
        Assert.Equal("The server refused the credentials (HTTP 401).", row.LastError);
        await dispatcher.StopAsync(Ct);
    }

    [Fact]
    public async Task AChannelThatKeepsFailingDoesNotHoldUpAnother()
    {
        var failing = await AddChannel();
        var healthy = await AddChannel();
        _sender.AlwaysFail(failing, new NotificationDeliveryException("down", transient: true));
        var dispatcher = Dispatcher();
        await dispatcher.StartAsync(Ct);

        dispatcher.Publish(Event());
        // The clock is capped below the failing channel's first retry (15 s), so a healthy channel queued behind it could never send in time.
        await AdvanceUntilAsync(() => _sender.Sent.Any(s => s.Channel == healthy), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(12));
        await RowSentAsync(healthy);
        await dispatcher.StopAsync(Ct);
    }

    [Fact]
    public async Task AChannelsQueueIsBoundedAndCountsWhatItDrops()
    {
        var channel = await AddChannel();
        var gate = _sender.Block(channel);
        var dispatcher = Dispatcher(new NotificationOptions { ChannelCapacity = 3, CoalesceWindow = TimeSpan.FromSeconds(1) });
        await dispatcher.StartAsync(Ct);

        dispatcher.Publish(Event(title: "first"));
        await AdvanceUntilAsync(() => _sender.Sent.Count >= 1, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        for (var i = 0; i < 10; i++)
        {
            dispatcher.Publish(Event(NotificationEventKind.UpstreamDown, $"later {i}"));
        }

        await UntilAsync(() => dispatcher.Dropped == 7 ? dispatcher : null);
        gate.SetResult();
        await dispatcher.StopAsync(Ct);
    }

    [Fact]
    public async Task TheEventQueueIsBoundedAndNeverBlocksTheCaller()
    {
        var dispatcher = Dispatcher(new NotificationOptions { EventCapacity = 2 });

        for (var i = 0; i < 5; i++)
        {
            dispatcher.Publish(Event());
        }

        Assert.Equal(3, dispatcher.Dropped);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AChannelDeletedWhileItsMessageWaitsIsSimplySkipped()
    {
        var channel = await AddChannel();
        var dispatcher = Dispatcher();
        await dispatcher.StartAsync(Ct);
        dispatcher.Publish(Event());
        await Task.Delay(100, Ct);

        await using (var context = _db.NewContext())
        {
            await context.NotificationChannels.Where(c => c.Id == channel).ExecuteDeleteAsync(Ct);
        }

        await AdvanceAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(_sender.Sent);
        Assert.Equal(0, dispatcher.QueueCount);
        await dispatcher.StopAsync(Ct);
    }

    [Fact]
    public async Task HasSubscribersLooksAtEnabledChannelsOnly()
    {
        await AddChannel(NotificationChannelKind.Webhook, enabled: false, NotificationEventKind.CertificateExpiring);
        await AddChannel(NotificationChannelKind.Webhook, enabled: true, NotificationEventKind.UpstreamDown);
        var dispatcher = Dispatcher();

        Assert.True(await dispatcher.HasSubscribersAsync(NotificationEventKind.UpstreamDown, Ct));
        Assert.False(await dispatcher.HasSubscribersAsync(NotificationEventKind.CertificateExpiring, Ct));
    }

    private sealed class Senders(INotificationSender sender) : INotificationSenders
    {
        public INotificationSender? Find(NotificationChannelKind kind) => sender;
    }

    /// <summary>Records every attempt (channel, message, clock) and fails the ones it is told to.</summary>
    private sealed class ScriptedSender(ManualTimeProvider clock) : INotificationSender
    {
        private readonly Lock _lock = new();
        private readonly List<(NotificationChannelId Channel, NotificationMessage Message, DateTimeOffset At)> _sent = [];
        private readonly Dictionary<NotificationChannelId, Queue<Exception>> _script = [];
        private readonly Dictionary<NotificationChannelId, Exception> _always = [];
        private readonly Dictionary<NotificationChannelId, TaskCompletionSource> _gates = [];

        public NotificationChannelKind Kind => NotificationChannelKind.Webhook;

        public List<(NotificationChannelId Channel, NotificationMessage Message, DateTimeOffset At)> Sent
        {
            get
            {
                lock (_lock)
                {
                    return [.. _sent];
                }
            }
        }

        public void Script(NotificationChannelId channel, params Exception[] failures) => _script[channel] = new Queue<Exception>(failures);

        public void AlwaysFail(NotificationChannelId channel, Exception failure) => _always[channel] = failure;

        public TaskCompletionSource Block(NotificationChannelId channel) => _gates[channel] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SendAsync(ChannelTarget channel, NotificationMessage message, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _sent.Add((channel.Id, message, clock.Now));
            }

            if (_gates.TryGetValue(channel.Id, out var gate))
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            if (_always.TryGetValue(channel.Id, out var failure) || (_script.TryGetValue(channel.Id, out var queue) && queue.TryDequeue(out failure)))
            {
                throw failure;
            }
        }
    }
}
