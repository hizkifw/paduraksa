using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Paduraksa.Application.Commands;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Data.Notifications;
using Paduraksa.Notifications;

namespace Paduraksa.Application.Notifications;

/// <summary>
/// Delivers events to the channels subscribed to them (PLAN §7.4 "As built (M9): health state and notifications"). Producers call
/// <see cref="Publish"/>, which only queues; a background loop addresses each event to every enabled subscribed channel, and each channel
/// has a queue and a delivery loop of its own, so a channel that is down never holds up the others.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bounded, never blocking.</b> Both queues drop their oldest entry when full and count it (<see cref="Dropped"/>, logged at Warning with a
/// running total), so a dead channel or a flood cannot grow memory or stall the caller.
/// </para>
/// <para>
/// <b>Retry.</b> A transient failure is retried after each of <see cref="NotificationOptions.RetryDelays"/>, with the channel's later messages waiting
/// behind it (order is kept); a permanent one (a refused login, a 404) is recorded at once. After the last retry the message is given up and the
/// channel shows "last error" until a delivery succeeds. Settings are read again for every attempt, so an edit or a deleted channel takes effect.
/// </para>
/// <para>
/// <b>Storms.</b> After the first message reaches a channel's queue the channel waits <see cref="NotificationOptions.CoalesceWindow"/> and then takes everything
/// queued: events of one kind that are worth summarising (health, expiry, ACME) go out as one digest when there are several, anything else
/// goes out as it is. Messages that pile up behind a retry are digested the same way.
/// </para>
/// </remarks>
public sealed partial class NotificationDispatcher : BackgroundService, INotificationEvents
{
    private static readonly HashSet<NotificationEventKind> Digestible =
    [
        NotificationEventKind.UpstreamDown, NotificationEventKind.UpstreamUp, NotificationEventKind.DestinationDown, NotificationEventKind.DestinationUp,
        NotificationEventKind.CertificateExpiring, NotificationEventKind.AcmeRenewalFailed, NotificationEventKind.AcmeRenewalRecovered,
    ];

    private readonly IDbContextFactory<ConfigDbContext> _contexts;
    private readonly INotificationSenders _senders;
    private readonly NotificationOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Channel<NotificationEvent> _events;
    private readonly ConcurrentDictionary<NotificationChannelId, ChannelQueue> _queues = new();
    private long _dropped;
    private long _delivered;
    private long _failed;

    internal NotificationDispatcher(
        IDbContextFactory<ConfigDbContext> contexts,
        INotificationSenders senders,
        NotificationOptions options,
        TimeProvider time,
        ILogger<NotificationDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(senders);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _contexts = contexts;
        _senders = senders;
        _options = options;
        _time = time;
        _logger = logger;
        _events = System.Threading.Channels.Channel.CreateBounded<NotificationEvent>(
            new BoundedChannelOptions(options.EventCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => CountDrop("event"));
    }

    /// <summary>The channels that have a queue now (a deleted channel's is dropped).</summary>
    internal int QueueCount => _queues.Count;

    /// <summary>Messages dropped because a queue was full, since the process started.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Messages delivered, and given up on, since the process started.</summary>
    public (long Delivered, long Failed) Totals => (Interlocked.Read(ref _delivered), Interlocked.Read(ref _failed));

    public void Publish(NotificationEvent notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        _ = _events.Writer.TryWrite(notification);
    }

    public async Task<bool> HasSubscribersAsync(NotificationEventKind kind, CancellationToken cancellationToken)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var channels = await context.NotificationChannels.AsNoTracking().Where(c => c.Enabled).ToListAsync(cancellationToken).ConfigureAwait(false);
            return channels.Any(c => NotificationMapping.Subscribes(c, kind));
        }
    }

    /// <summary>Waits until nothing is queued or in flight, for a test that moves the clock.</summary>
    internal async Task QuiesceAsync(CancellationToken cancellationToken)
    {
        while (_events.Reader.Count > 0 || _queues.Values.Any(q => q.Busy))
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var notification in _events.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await RouteAsync(notification, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogRouteFailed(ex, notification.Kind.ToName());
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            foreach (var queue in _queues.Values)
            {
                await queue.StopAsync().ConfigureAwait(false);
                queue.Dispose();
            }
        }
    }

    private async Task RouteAsync(NotificationEvent notification, CancellationToken cancellationToken)
    {
        List<NotificationChannelRow> channels;
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            channels = [.. (await context.NotificationChannels.AsNoTracking().Where(c => c.Enabled).ToListAsync(cancellationToken).ConfigureAwait(false))
                .Where(c => NotificationMapping.Subscribes(c, notification.Kind))];
        }

        foreach (var channel in channels)
        {
            var queue = _queues.GetOrAdd(channel.Id, id => new ChannelQueue(this, id));
            queue.Enqueue(notification, cancellationToken);
        }
    }

    /// <summary>Drops a deleted channel's queue so the table holds only channels that exist; a message already waiting in it is dropped with it.</summary>
    private void Forget(NotificationChannelId id)
    {
        if (_queues.TryRemove(id, out var queue))
        {
            queue.Retire();
        }
    }

    private void CountDrop(string what)
    {
        var total = Interlocked.Increment(ref _dropped);
        if (total is 1 || total % 100 == 0)
        {
            LogDropped(what, total);
        }
    }

    /// <summary>One channel's queue and the loop that empties it, started when the first message arrives and ended when it is empty.</summary>
    private sealed class ChannelQueue(NotificationDispatcher owner, NotificationChannelId id) : IDisposable
    {
        private readonly Lock _lock = new();
        private readonly Queue<NotificationEvent> _items = new();
        private readonly CancellationTokenSource _stop = new();
        private Task _loop = Task.CompletedTask;
        private bool _running;
        private bool _retired;

        public bool Busy
        {
            get
            {
                lock (_lock)
                {
                    return _running || _items.Count > 0;
                }
            }
        }

        public void Enqueue(NotificationEvent notification, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                if (_retired)
                {
                    return;
                }

                if (_items.Count >= owner._options.ChannelCapacity)
                {
                    _ = _items.Dequeue();
                    owner.CountDrop("channel message");
                }

                _items.Enqueue(notification);
                if (_running || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _running = true;
                _loop = Task.Run(RunAsync, CancellationToken.None);
            }
        }

        public async Task StopAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            await _loop.ConfigureAwait(false);
        }

        public void Dispose() => _stop.Dispose();

        /// <summary>Called from this queue's own loop when its channel is gone: nothing is left to send, so stop waiting and release the token.</summary>
        public void Retire()
        {
            lock (_lock)
            {
                _retired = true;
                _items.Clear();
                _running = false;
            }

            _stop.Cancel();
        }

        private async Task RunAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(owner._options.CoalesceWindow, owner._time, _stop.Token).ConfigureAwait(false);
                    List<NotificationEvent> batch;
                    lock (_lock)
                    {
                        if (_items.Count == 0)
                        {
                            _running = false;
                            return;
                        }

                        batch = [.. _items];
                        _items.Clear();
                    }

                    foreach (var message in Coalesce(batch))
                    {
                        await owner.DeliverAsync(id, message, _stop.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                lock (_lock)
                {
                    _running = false;
                }
            }
            finally
            {
                // A retired queue is no longer in the table, so nothing else will dispose it.
                if (_retired)
                {
                    _stop.Dispose();
                }
            }
        }

        private static IEnumerable<NotificationEvent> Coalesce(List<NotificationEvent> batch)
        {
            HashSet<NotificationEventKind> done = [];
            foreach (var item in batch)
            {
                if (!Digestible.Contains(item.Kind))
                {
                    yield return item;
                    continue;
                }

                if (!done.Add(item.Kind))
                {
                    continue;
                }

                var same = batch.Where(e => e.Kind == item.Kind).ToList();
                yield return same.Count == 1 ? item : NotificationText.Digest(same);
            }
        }
    }

    private async Task DeliverAsync(NotificationChannelId id, NotificationEvent notification, CancellationToken cancellationToken)
    {
        var message = NotificationAddress.Message(notification, _options);
        for (var attempt = 0; ; attempt++)
        {
            NotificationChannelRow? row;
            var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                row = await context.NotificationChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
            }

            if (row is null)
            {
                // Deleted: its queue (and the token it waits on) goes with it, once this loop has finished with it.
                Forget(id);
                return;
            }

            if (!row.Enabled || _senders.Find(row.Kind) is not { } sender)
            {
                return;
            }

            string? failure;
            var transient = true;
            try
            {
                await sender.SendAsync(NotificationMapping.ToTarget(row), message, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _delivered);
                await RecordAsync(id, error: null, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (NotificationDeliveryException ex)
            {
                failure = ex.Message;
                transient = ex.Transient;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not one of ours: its text could hold anything, so only its type is kept.
                LogUnexpected(id, ex.GetType().Name);
                failure = "The notification could not be sent.";
            }

            if (!transient || attempt >= _options.RetryDelays.Count)
            {
                Interlocked.Increment(ref _failed);
                LogGivenUp(id, notification.Kind.ToName(), failure);
                await RecordAsync(id, failure, cancellationToken).ConfigureAwait(false);
                return;
            }

            await Task.Delay(_options.RetryDelays[attempt], _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task RecordAsync(NotificationChannelId id, string? error, CancellationToken cancellationToken) =>
        NotificationChannelState.RecordAsync(_contexts, _time, id, error, _logger, cancellationToken);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification queue full: dropped the oldest {What}; {Total} dropped since the process started.")]
    private partial void LogDropped(string what, long total);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification channel {Channel} gave up on a {Kind} message: {Reason}")]
    private partial void LogGivenUp(NotificationChannelId channel, string kind, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A {Kind} event could not be addressed to its channels.")]
    private partial void LogRouteFailed(Exception exception, string kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification channel {Channel}: a sender failed unexpectedly ({Type}).")]
    private partial void LogUnexpected(NotificationChannelId channel, string type);
}
