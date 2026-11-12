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

/// <summary>Wakes a channel's worker now, for a delivery that was made due outside the flusher (a manual retry).</summary>
internal interface IDeliveryWake
{
    void Wake(NotificationChannelId channel);
}

/// <summary>
/// Delivers events to the channels subscribed to them, durably (PLAN §7.4 "As built: durable delivery and history"). Producers call
/// <see cref="Publish"/>, which only puts the event in a small in-memory buffer; the flusher writes the buffer to the database in batches, with one
/// pending delivery per subscribed channel, and each channel's worker takes its due deliveries from there. What is in the database survives a stop
/// or a crash and resumes at the next start.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one window.</b> A crash loses what the buffer holds: at most the events of one <see cref="NotificationOptions.FlushInterval"/> (or what a database
/// that cannot be written to has held back, bounded by <see cref="NotificationOptions.EventCapacity"/>). A graceful stop flushes it. The buffer is
/// bounded and drops its oldest event when full (<see cref="Dropped"/>), so a publisher never waits for the database.
/// </para>
/// <para>
/// <b>At least once.</b> A crash between a send and the row update sends again at the next start, so a delivery can arrive twice; a webhook carries the event
/// ID (the digest's, for a digest) to drop the second. <b>Retry</b> is stored in the row (attempts, next attempt) and is read off <see cref="NotificationOptions.RetryDelays"/>:
/// a transient failure waits, a permanent one is final at once. While a channel has a retry waiting, its newer deliveries wait behind it (order is kept) and
/// are digested with it when it comes due. <b>Storms:</b> a worker woken by new deliveries waits <see cref="NotificationOptions.CoalesceWindow"/> and then takes everything
/// due: events of one digestible kind (health, expiry, ACME) go out as one digest when there are several.
/// </para>
/// </remarks>
public sealed partial class NotificationDispatcher : BackgroundService, INotificationEvents, IDeliveryWake
{
    private readonly IDbContextFactory<ConfigDbContext> _contexts;
    private readonly INotificationSenders _senders;
    private readonly NotificationOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly NotificationOutbox _outbox;
    private readonly Channel<(NotificationEvent Event, DateTimeOffset At)> _buffer;
    private readonly ConcurrentDictionary<NotificationChannelId, ChannelWorker> _workers = new();
    private readonly CancellationTokenSource _workersStop = new();
    private long _routed;
    private long _flushFailures;
    private volatile bool _recovered;

    internal NotificationDispatcher(
        IDbContextFactory<ConfigDbContext> contexts,
        INotificationSenders senders,
        NotificationOptions options,
        TimeProvider time,
        ILogger<NotificationDispatcher> logger,
        NotificationStats stats)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(senders);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(stats);
        _contexts = contexts;
        _senders = senders;
        _options = options;
        _time = time;
        _logger = logger;
        Stats = stats;
        _outbox = new NotificationOutbox(contexts, Stats);
        _buffer = System.Threading.Channels.Channel.CreateBounded<(NotificationEvent, DateTimeOffset)>(
            new BoundedChannelOptions(options.EventCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => CountDrop());
    }

    /// <summary>The figures <c>/metrics</c> reads.</summary>
    public NotificationStats Stats { get; }

    /// <summary>The channels that have a worker now (a deleted channel's goes).</summary>
    internal int QueueCount => _workers.Count;

    /// <summary>Events dropped because the in-memory buffer was full, since the process started.</summary>
    public long Dropped => Stats.BufferDropped;

    /// <summary>Deliveries sent, and given up on, since the process started.</summary>
    public (long Delivered, long Failed) Totals => (Sum(NotificationDeliveryState.Sent), Sum(NotificationDeliveryState.Failed));

    /// <summary>Events written to the database so far, for a test that must not move the clock before the flusher is done (a worker's window starts at its first wake).</summary>
    internal long Routed => Interlocked.Read(ref _routed);

    /// <summary>Puts the event in the buffer; the flusher stores it. Never waits for the database and never throws.</summary>
    public void Publish(NotificationEvent notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        _ = _buffer.Writer.TryWrite((notification, _time.GetUtcNow()));
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

    void IDeliveryWake.Wake(NotificationChannelId channel) => Wake([channel]);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Task upkeep = Task.CompletedTask;
        List<BufferedEvent> carry = [];
        try
        {
            await RecoverAsync(stoppingToken).ConfigureAwait(false);
            upkeep = Task.Run(() => UpkeepAsync(stoppingToken), CancellationToken.None);
            await FlushAsync(carry, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            await FlushRemainingAsync(carry).ConfigureAwait(false);
            await _workersStop.CancelAsync().ConfigureAwait(false);
            await upkeep.ConfigureAwait(false);
            await Task.WhenAll(_workers.Values.Select(w => w.Stopped)).ConfigureAwait(false);
            _workersStop.Dispose();
        }
    }

    /// <summary>
    /// What the last process left: deliveries of a channel that is gone are dropped, stale ones expire, and a worker starts for each channel with something
    /// pending. A failure is logged and tried again by every upkeep tick until it works.
    /// </summary>
    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = _time.GetUtcNow();
            Wake(await _outbox.RecoverAsync(now - _options.MaxDeliveryAge, now, cancellationToken).ConfigureAwait(false));
            await _outbox.RefreshPendingAsync(cancellationToken).ConfigureAwait(false);
            _recovered = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRecoverFailed(ex);
        }
    }

    /// <summary>
    /// The flusher. A burst gathers for <see cref="NotificationOptions.FlushInterval"/> of real time (a throughput knob: it must not wait for a clock a
    /// test holds still), then one transaction stores the batch. A write that fails keeps the batch and tries again, so an event that was drained is not lost
    /// to a locked database; what arrives meanwhile waits in the bounded buffer.
    /// </summary>
    private async Task FlushAsync(List<BufferedEvent> carry, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (carry.Count == 0 && !await _buffer.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            if (carry.Count + _buffer.Reader.Count < _options.FlushBatchSize)
            {
                await Task.Delay(_options.FlushInterval, cancellationToken).ConfigureAwait(false);
            }

            Drain(carry);
            if (!await TryStoreAsync(carry, cancellationToken).ConfigureAwait(false))
            {
                await Task.Delay(_options.FlushInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The stop's flush: everything still buffered is written once; if the database refuses, what is lost is counted in the log.</summary>
    private async Task FlushRemainingAsync(List<BufferedEvent> carry)
    {
        Drain(carry, all: true);
        while (carry.Count > 0)
        {
            var batch = carry.Take(_options.FlushBatchSize).ToList();
            try
            {
                Wake(await _outbox.StoreAsync(batch, CancellationToken.None).ConfigureAwait(false));
                _ = Interlocked.Add(ref _routed, batch.Count);
                carry.RemoveRange(0, batch.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogLostAtStop(ex, carry.Count);
                return;
            }
        }
    }

    private void Drain(List<BufferedEvent> carry, bool all = false)
    {
        while ((all || carry.Count < _options.FlushBatchSize) && _buffer.Reader.TryRead(out var item))
        {
            carry.Add(new BufferedEvent(NotificationEventId.New(), item.Event, item.At));
        }
    }

    private async Task<bool> TryStoreAsync(List<BufferedEvent> carry, CancellationToken cancellationToken)
    {
        if (carry.Count == 0)
        {
            return true;
        }

        try
        {
            Wake(await _outbox.StoreAsync(carry, cancellationToken).ConfigureAwait(false));
            _ = Interlocked.Add(ref _routed, carry.Count);
            carry.Clear();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var total = Interlocked.Increment(ref _flushFailures);
            if (total is 1 || total % 100 == 0)
            {
                LogFlushFailed(ex, carry.Count, total);
            }

            return false;
        }
    }

    /// <summary>On a slow timer: finishes a recovery that failed, wakes every channel with something due, counts the pending deliveries again and prunes the history; a failure waits for the next tick.</summary>
    private async Task UpkeepAsync(CancellationToken cancellationToken)
    {
        try
        {
            using PeriodicTimer timer = new(_options.PollInterval, _time);
            var nextPrune = _time.GetUtcNow();
            do
            {
                try
                {
                    if (!_recovered)
                    {
                        await RecoverAsync(cancellationToken).ConfigureAwait(false);
                    }

                    // Whatever happened at start (or since), a channel with something due has a worker: the safety net under every other wake.
                    Wake(await _outbox.ChannelsWithDueAsync(_time.GetUtcNow(), cancellationToken).ConfigureAwait(false));
                    await _outbox.RefreshPendingAsync(cancellationToken).ConfigureAwait(false);
                    if (_time.GetUtcNow() >= nextPrune)
                    {
                        var removed = await PruneAsync(cancellationToken).ConfigureAwait(false);
                        nextPrune = _time.GetUtcNow() + _options.PruneInterval;
                        if (removed > 0)
                        {
                            LogPruned(removed, _options.HistoryRetention);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogUpkeepFailed(ex);
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    /// <summary>One retention pass; internal for tests, which drive it without the timer.</summary>
    internal Task<int> PruneAsync(CancellationToken cancellationToken) =>
        _outbox.PruneAsync(_time.GetUtcNow() - _options.HistoryRetention, _options.PruneBatchSize, cancellationToken);

    private void Wake(IEnumerable<NotificationChannelId> channels)
    {
        if (_workersStop.IsCancellationRequested)
        {
            return;
        }

        foreach (var channel in channels)
        {
            var worker = _workers.GetOrAdd(channel, id => new ChannelWorker(this, id));
            worker.Wake();
            worker.Start();
        }
    }

    /// <summary>A worker whose channel is gone leaves the table, so it holds only channels that exist.</summary>
    private void Forget(NotificationChannelId id) => _ = _workers.TryRemove(id, out _);

    private void CountDrop()
    {
        Stats.BufferDrop();
        var total = Stats.BufferDropped;
        if (total is 1 || total % 100 == 0)
        {
            LogDropped(total);
        }
    }

    private long Sum(NotificationDeliveryState outcome) => NotificationStats.ChannelKinds.Sum(kind => Stats.Ended(kind, outcome));

    /// <summary>One channel's worker: it sleeps until woken, until its next retry or until the poll, then takes what is due and sends it.</summary>
    private sealed class ChannelWorker(NotificationDispatcher owner, NotificationChannelId id) : IDisposable
    {
        private readonly SemaphoreSlim _wake = new(0, 1);
        private int _started;
        private NotificationChannelKind? _kind;
        private DateTimeOffset? _lastSendAt;

        public Task Stopped { get; private set; } = Task.CompletedTask;

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                Stopped = Task.Run(() => RunAsync(owner._workersStop.Token), CancellationToken.None);
            }
        }

        public void Wake()
        {
            try
            {
                _ = _wake.Release();
            }
            catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException)
            {
                // Already woken (one pass takes everything due), or retired with its channel.
            }
        }

        public void Dispose() => _wake.Dispose();

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            var woken = await _wake.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
            var wait = TimeSpan.Zero;
            try
            {
                while (true)
                {
                    if (wait > TimeSpan.Zero)
                    {
                        woken = await WaitAsync(wait, cancellationToken).ConfigureAwait(false);
                    }

                    if (woken && Remaining() is { } rest)
                    {
                        // The channel sent a moment ago: what arrives now waits out the rest of the window and goes together, as a digest when it is several.
                        await Task.Delay(rest, owner._time, cancellationToken).ConfigureAwait(false);
                    }

                    if (await PassAsync(cancellationToken).ConfigureAwait(false) is not { } next)
                    {
                        return;
                    }

                    wait = next;
                    woken = false;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Stopping: whatever was taken stays pending and resumes at the next start.
            }
            finally
            {
                Dispose();
            }
        }

        /// <summary>
        /// What is left of the coalescing window, null when there is none: <b>the first send goes at once</b>, so a channel that has not sent within the last
        /// <see cref="NotificationOptions.CoalesceWindow"/> sends what is due without waiting, and only a channel that has (a burst) waits out the window.
        /// </summary>
        private TimeSpan? Remaining()
        {
            if (_lastSendAt is not { } last)
            {
                return null;
            }

            var rest = owner._options.CoalesceWindow - (owner._time.GetUtcNow() - last);
            return rest > TimeSpan.Zero ? rest : null;
        }

        /// <summary>Waits for a wake or for <paramref name="wait"/> on the process clock; true when woken.</summary>
        private async Task<bool> WaitAsync(TimeSpan wait, CancellationToken cancellationToken)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var signal = _wake.WaitAsync(cts.Token);
            var timer = Task.Delay(wait, owner._time, cts.Token);
            _ = await Task.WhenAny(signal, timer).ConfigureAwait(false);
            await cts.CancelAsync().ConfigureAwait(false);
            var woken = signal.IsCompletedSuccessfully;
            await Task.WhenAll(signal, timer).AsCancellationSwallowed().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return woken;
        }

        /// <summary>
        /// One look at the channel: read its settings again (an edit or a deleted channel takes effect), then send what is due, unless a retry is waiting.
        /// Returns how long to sleep, or null when the channel is gone.
        /// </summary>
        private async Task<TimeSpan?> PassAsync(CancellationToken cancellationToken)
        {
            var poll = owner._options.PollInterval;
            try
            {
                var channel = await ReadChannelAsync(cancellationToken).ConfigureAwait(false);
                if (channel is null)
                {
                    _ = await owner._outbox.DropPendingAsync(id, _kind, NotificationOutbox.ChannelDeleted, owner._time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                    owner.Forget(id);
                    return null;
                }

                _kind = channel.Kind;
                var now = owner._time.GetUtcNow();
                if (!channel.Enabled)
                {
                    _ = await owner._outbox.DropPendingAsync(id, channel.Kind, NotificationOutbox.ChannelPaused, now, cancellationToken).ConfigureAwait(false);
                    return poll;
                }

                if (await owner._outbox.RetryWaitingUntilAsync(id, now, cancellationToken).ConfigureAwait(false) is { } until)
                {
                    return Within(until - now, poll);
                }

                var groups = await owner._outbox.ClaimAsync(id, channel.Kind, now, now - owner._options.MaxDeliveryAge, owner._options.ClaimBatchSize, cancellationToken).ConfigureAwait(false);
                if (groups.Count > 0)
                {
                    _lastSendAt = now;
                }

                for (var i = 0; i < groups.Count; i++)
                {
                    if (i > 0 && await ReadChannelAsync(cancellationToken).ConfigureAwait(false) is { Enabled: true } current)
                    {
                        channel = current;
                    }
                    else if (i > 0)
                    {
                        // Deleted or paused while this pass sent: the next pass drops what is left.
                        return TimeSpan.Zero;
                    }

                    if (await SendAsync(channel, groups[i], cancellationToken).ConfigureAwait(false) is { } retry)
                    {
                        // Its newer deliveries wait behind it: this pass ends here.
                        return Within(retry, poll);
                    }
                }

                return groups.Sum(g => g.Rows.Count) >= owner._options.ClaimBatchSize ? TimeSpan.Zero : poll;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                owner.LogWorkerFailed(ex, id);
                return poll;
            }
        }

        /// <summary>Sends one group and records it. Returns the wait before its retry, or null when it ended (sent, or given up).</summary>
        private async Task<TimeSpan?> SendAsync(NotificationChannelRow channel, DeliveryGroup group, CancellationToken cancellationToken)
        {
            if (owner._senders.Find(channel.Kind) is not { } sender)
            {
                await EndAsync(channel, group, NotificationDeliveryState.Failed, "This build cannot send to that kind of channel.", cancellationToken).ConfigureAwait(false);
                return null;
            }

            var message = NotificationAddress.Message(group.Message, owner._options) with { EventId = group.EventId };
            string? failure = null;
            var transient = true;
            try
            {
                await sender.SendAsync(NotificationMapping.ToTarget(channel), message, cancellationToken).ConfigureAwait(false);
            }
            catch (NotificationDeliveryException ex)
            {
                failure = ex.Message;
                transient = ex.Transient;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not one of ours: its text could hold anything, so only its type is kept.
                owner.LogUnexpected(id, ex.GetType().Name);
                failure = "The notification could not be sent.";
            }

            if (failure is null)
            {
                // Delivered: nothing that goes wrong in writing it down may count as a failure (it would be sent again), so this is outside the send's try.
                await EndAsync(channel, group, NotificationDeliveryState.Sent, null, cancellationToken).ConfigureAwait(false);
                return null;
            }

            var attempts = group.Attempts + 1;
            var delays = owner._options.RetryDelays;
            if (!transient || attempts > delays.Count)
            {
                owner.LogGivenUp(id, group.Message.Kind.ToName(), failure);
                await EndAsync(channel, group, NotificationDeliveryState.Failed, failure, cancellationToken).ConfigureAwait(false);
                return null;
            }

            var now = owner._time.GetUtcNow();
            var delay = delays[attempts - 1];
            _ = await owner._outbox.RecordAsync(group, NotificationDeliveryState.Pending, failure, now, now + delay, cancellationToken).ConfigureAwait(false);
            return delay;
        }

        /// <summary>
        /// A group ended: sent, or given up. The rows are written first, then the channel's last sent and last error. A failure of either is logged and goes no
        /// further: the message was delivered (or given up on) whatever the bookkeeping did, and rows still pending are sent again, which at-least-once allows.
        /// </summary>
        private async Task EndAsync(NotificationChannelRow channel, DeliveryGroup group, NotificationDeliveryState state, string? error, CancellationToken cancellationToken)
        {
            try
            {
                var changed = await owner._outbox.RecordAsync(group, state, error, owner._time.GetUtcNow(), null, cancellationToken).ConfigureAwait(false);
                owner.Stats.Ended(channel.Kind, state, changed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                owner.LogRecordFailed(ex, id);
                return;
            }

            try
            {
                await NotificationChannelState.RecordAsync(owner._contexts, owner._time, id, error, owner._logger, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                owner.LogRecordFailed(ex, id);
            }
        }

        private async Task<NotificationChannelRow?> ReadChannelAsync(CancellationToken cancellationToken)
        {
            var context = await owner._contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                return await context.NotificationChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
            }
        }

        private static TimeSpan Within(TimeSpan wait, TimeSpan poll) => wait <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : wait < poll ? wait : poll;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification buffer full: dropped the oldest event; {Total} dropped since the process started.")]
    private partial void LogDropped(long total);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification channel {Channel} gave up on a {Kind} message: {Reason}")]
    private partial void LogGivenUp(NotificationChannelId channel, string kind, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} buffered notification event(s) could not be saved ({Failures} failed attempt(s) so far); they are kept and tried again.")]
    private partial void LogFlushFailed(Exception exception, int count, long failures);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Count} buffered notification event(s) could not be saved at shutdown and are lost.")]
    private partial void LogLostAtStop(Exception exception, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification deliveries left by the last run could not be recovered; pending ones are tried as they come due.")]
    private partial void LogRecoverFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification upkeep (queue depth, history pruning) failed; it is tried again at the next interval.")]
    private partial void LogUpkeepFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} notification event(s) past the history retention ({Retention}).")]
    private partial void LogPruned(int count, TimeSpan retention);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification channel {Channel}: its worker failed; it tries again at the next interval.")]
    private partial void LogWorkerFailed(Exception exception, NotificationChannelId channel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification channel {Channel}: a delivery's outcome could not be written down; a delivered message may be sent again.")]
    private partial void LogRecordFailed(Exception exception, NotificationChannelId channel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification channel {Channel}: a sender failed unexpectedly ({Type}).")]
    private partial void LogUnexpected(NotificationChannelId channel, string type);
}

internal static class TaskExtensions
{
    /// <summary>Awaits <paramref name="task"/> and ignores its cancellation: the losing half of a race that was cancelled on purpose.</summary>
    public static async Task AsCancellationSwallowed(this Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: the race's loser.
        }
    }
}
