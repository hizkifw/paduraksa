using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Paduraksa.Application.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Data.Notifications;
using Paduraksa.Notifications;
using Paduraksa.Tests.Data.Config;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Notifications;

/// <summary>
/// A real config database, a manual clock and a scripted sender for the outbox tests. The clock moves only when a test moves it, but the flusher and the
/// workers run on real threads, so what a test expects is waited for in real time and the clock is moved until the thing has happened (the pattern of
/// <see cref="NotificationDispatcherTests"/>): never by one fixed amount.
/// </summary>
public abstract class OutboxTestBase : IAsyncLifetime
{
    protected static readonly DateTimeOffset T0 = new(2026, 11, 12, 14, 0, 0, TimeSpan.Zero);

    protected static readonly TimeSpan Flush = TimeSpan.FromMilliseconds(10);

    private readonly List<NotificationDispatcher> _started = [];

    internal ManualTimeProvider Time { get; } = new(T0);

    internal ScriptedSender Sender { get; }

    internal TestConfigDatabase Db { get; private set; } = null!;

    protected OutboxTestBase() => Sender = new ScriptedSender(Time);

    public async ValueTask InitializeAsync() => Db = await CreateAsync();

    public async ValueTask DisposeAsync()
    {
        foreach (var dispatcher in _started)
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }

        await Db.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    protected static NotificationOptions Options(
        TimeSpan? flush = null,
        int eventCapacity = 1000,
        int flushBatch = 100,
        TimeSpan? maxAge = null,
        TimeSpan? retention = null,
        int pruneBatch = 500,
        int claimBatch = 500) => new()
        {
            AdminUrl = new Uri("https://admin.example.com"),
            FlushInterval = flush ?? Flush,
            EventCapacity = eventCapacity,
            FlushBatchSize = flushBatch,
            MaxDeliveryAge = maxAge ?? TimeSpan.FromHours(1),
            HistoryRetention = retention ?? TimeSpan.FromDays(14),
            PruneBatchSize = pruneBatch,
            ClaimBatchSize = claimBatch,
        };

    /// <summary>A dispatcher on the test database, stopped with the test. Not started: the test starts it.</summary>
    internal NotificationDispatcher Dispatcher(NotificationOptions? options = null, IDbContextFactory<ConfigDbContext>? factory = null, NotificationStats? stats = null)
    {
        NotificationDispatcher dispatcher = new(factory ?? Db.Factory, new Senders(Sender), options ?? Options(), Time, NullLogger<NotificationDispatcher>.Instance, stats ?? new NotificationStats());
        _started.Add(dispatcher);
        return dispatcher;
    }

    protected async Task<NotificationChannelId> AddChannel(NotificationChannelKind kind = NotificationChannelKind.Webhook, bool enabled = true, params NotificationEventKind[] events)
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
        await using var context = Db.NewContext();
        context.NotificationChannels.Add(row);
        await context.SaveChangesAsync(Ct);
        return row.Id;
    }

    protected static NotificationEvent Event(NotificationEventKind kind = NotificationEventKind.UpstreamDown, string title = "Upstream media is down") =>
        new(kind, title, "body", NotificationSeverity.Critical, T0, "/upstreams", [new("upstreamId", "ups_1")], "ups_1");

    /// <summary>What a process that stopped leaves: an event and a pending delivery to <paramref name="channel"/>, as if the flusher had written them <paramref name="age"/> ago.</summary>
    protected async Task<(NotificationEventId Event, NotificationDeliveryId Delivery)> LeftBehind(
        NotificationChannelId channel,
        TimeSpan age,
        NotificationEventKind kind = NotificationEventKind.UpstreamDown,
        string title = "Upstream media is down",
        DateTimeOffset? nextAttemptAt = null,
        int attempts = 0)
    {
        NotificationEventRow row = new()
        {
            Id = NotificationEventId.New(),
            Kind = kind,
            Subject = "ups_1",
            Title = title,
            Body = "body",
            Severity = (int)NotificationSeverity.Critical,
            LinkPath = "/upstreams",
            Data = NotificationDataJson("upstreamId", "ups_1"),
            OccurredAt = T0 - age,
            CreatedAt = T0 - age,
        };
        NotificationDeliveryRow delivery = new()
        {
            Id = NotificationDeliveryId.New(),
            EventId = row.Id,
            ChannelId = channel,
            NextAttemptAt = nextAttemptAt ?? (T0 - age),
            Attempts = attempts,
        };
        await using var context = Db.NewContext();
        context.NotificationEvents.Add(row);
        context.NotificationDeliveries.Add(delivery);
        await context.SaveChangesAsync(Ct);
        return (row.Id, delivery.Id);
    }

    private static string NotificationDataJson(string key, string value) => $"[[\"{key}\",\"{value}\"]]";

    internal async Task<List<NotificationEventRow>> Events()
    {
        await using var context = Db.NewContext();
        return await context.NotificationEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync(Ct);
    }

    internal async Task<List<NotificationDeliveryRow>> Deliveries()
    {
        await using var context = Db.NewContext();
        return await context.NotificationDeliveries.AsNoTracking().OrderBy(d => d.Id).ToListAsync(Ct);
    }

    internal async Task<NotificationChannelRow> Channel(NotificationChannelId id)
    {
        await using var context = Db.NewContext();
        return await context.NotificationChannels.AsNoTracking().SingleAsync(c => c.Id == id, Ct);
    }

    /// <summary>Moves the clock in <paramref name="step"/>s until <paramref name="done"/>, at most <paramref name="max"/>, giving the loops real time to run between steps.</summary>
    protected async Task AdvanceUntilAsync(Func<bool> done, TimeSpan step, TimeSpan max)
    {
        for (var spent = TimeSpan.Zero; !done() && spent < max; spent += step)
        {
            await Task.Delay(8, Ct);
            Time.Advance(step);
        }

        await SettledAsync(done);
    }

    /// <summary>Moves the clock by <paramref name="total"/> in <paramref name="step"/>s without waiting for anything: for "and nothing happened meanwhile".</summary>
    protected async Task AdvanceAsync(TimeSpan total, TimeSpan step)
    {
        for (var spent = TimeSpan.Zero; spent < total; spent += step)
        {
            await Task.Delay(8, Ct);
            Time.Advance(step);
        }

        await Task.Delay(50, Ct);
    }

    protected static async Task SettledAsync(Func<bool> done) => await UntilAsync(() => done() ? new object() : null);

    protected static async Task<T> UntilAsync<T>(Func<T?> read)
        where T : class
    {
        for (var i = 0; i < 400; i++)
        {
            if (read() is { } value)
            {
                return value;
            }

            await Task.Delay(10, Ct);
        }

        throw new TimeoutException();
    }

    /// <summary>Waits (in real time) for a database condition.</summary>
    protected static async Task UntilAsync(Func<Task<bool>> done)
    {
        for (var i = 0; i < 400; i++)
        {
            if (await done())
            {
                return;
            }

            await Task.Delay(10, Ct);
        }

        throw new TimeoutException();
    }

    internal sealed class Senders(INotificationSender sender) : INotificationSenders
    {
        public INotificationSender? Find(NotificationChannelKind kind) => sender;
    }

    /// <summary>Records every attempt (channel, message, clock) and fails the ones it is told to.</summary>
    internal sealed class ScriptedSender(ManualTimeProvider clock) : INotificationSender
    {
        private readonly Lock _lock = new();
        private readonly List<(NotificationChannelId Channel, NotificationMessage Message, DateTimeOffset At)> _sent = [];
        private readonly Dictionary<NotificationChannelId, Queue<Exception>> _script = [];
        private readonly Dictionary<NotificationChannelId, Exception> _always = [];
        private Func<NotificationMessage, CancellationToken, Task>? _onSend;

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

        public void Script(NotificationChannelId channel, params Exception[] failures)
        {
            lock (_lock)
            {
                _script[channel] = new Queue<Exception>(failures);
            }
        }

        public void AlwaysFail(NotificationChannelId channel, Exception failure)
        {
            lock (_lock)
            {
                _always[channel] = failure;
            }
        }

        public void Heal(NotificationChannelId channel)
        {
            lock (_lock)
            {
                _ = _always.Remove(channel);
            }
        }

        /// <summary>Runs <paramref name="hook"/> after a send is recorded and before it answers: a place to stop the process in the middle of a send.</summary>
        public void OnSend(Func<NotificationMessage, CancellationToken, Task>? hook) => _onSend = hook;

        public async Task SendAsync(ChannelTarget channel, NotificationMessage message, CancellationToken cancellationToken)
        {
            Exception? failure;
            lock (_lock)
            {
                _sent.Add((channel.Id, message, clock.Now));
                if (!_always.TryGetValue(channel.Id, out failure) && _script.TryGetValue(channel.Id, out var queue))
                {
                    _ = queue.TryDequeue(out failure);
                }
            }

            if (_onSend is { } hook)
            {
                await hook(message, cancellationToken);
            }

            if (failure is not null)
            {
                throw failure;
            }
        }
    }
}
