using System.Threading.Channels;

using Paduraksa.Core.Options;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Proxy.Logging;

/// <summary>
/// The live tail's hub (docs/design/observability.md "The live tail (M9)"): the most recent access-log entries in a fixed-size buffer, and a bounded channel per
/// subscriber. <see cref="AccessLogDrainService"/>, the ring's one reader, offers each entry here after it has written the
/// file's line, so the request path doesn't know the tail exists: no new work per request, and the entries a host keeps out of
/// the file (its access log is off) never get this far, so they are not in the tail either.
/// </summary>
/// <remarks>
/// <para><b>Copied, and shown under the file's rules.</b> The ring's slot is reused, so an offered entry is copied into the
/// buffer as the struct it is, each string cut at its cap (the query at the file's query cap). An entry takes a few hundred bytes
/// to a KiB typically and about 8 KiB at most (every field at its cap), so the buffer is 2 to 4 MiB typically and 32 MiB at the
/// worst at the default size, up to 512 MiB at <see cref="MaximumCapacity"/>. Nothing is converted until someone looks: a
/// subscriber's entries and a backlog become <see cref="AccessLogTailEntry"/>, capped, escaped and redacted exactly as the line
/// is, when they are read. That is what keeps the tail free when nobody watches: the drain stores a struct and allocates
/// nothing.</para>
/// <para><b>A slow subscriber costs the drain nothing.</b> A subscriber's channel is bounded and drops its oldest entry when
/// full, counting it (<see cref="AccessLogTailSubscription.Skipped"/>); a write never waits. The number of subscribers is
/// bounded too (<see cref="MaxSubscribers"/>), so a client that opens streams in a loop is refused rather than served.</para>
/// <para><b>One lock, held briefly.</b> The drain takes it to store the entry and hand it to the subscribers' channels (a write to
/// a bounded channel never waits); <see cref="Subscribe"/> and <see cref="Recent"/> take it to copy the buffer's structs (a plain
/// array copy, about a millisecond at the largest size) and, for a subscriber, to register it in the same step, so no entry is
/// missed or shown twice between the backlog and the stream. Conversion, redaction and filtering happen outside it. The drain
/// never waits for a reader's channel, and waits for a reader's call only for that copy. The request path never takes it.</para>
/// </remarks>
public sealed class AccessLogTail
{
    /// <summary>Entries held by default.</summary>
    public const int DefaultCapacity = 4_096;

    /// <summary>The least the buffer may hold (the option validator's floor).</summary>
    public const int MinimumCapacity = 64;

    /// <summary>The most the buffer may hold (the validator's ceiling): an entry is capped at about 8 KiB, so the largest buffer can reach about 512 MiB.</summary>
    public const int MaximumCapacity = 65_536;

    /// <summary>The subscribers served at once: dashboard tabs and API streams together.</summary>
    public const int MaxSubscribers = 32;

    /// <summary>Entries a subscriber's channel holds before it starts dropping its oldest.</summary>
    public const int SubscriberCapacity = 2_048;

    private readonly Lock _gate = new();
    private readonly AccessLogEntry[] _entries;
    private readonly LiveOptions<AccessLogFormatOptions> _options;
    private readonly SnapshotStore? _snapshots;
    private AccessLogTailSubscription[] _subscribers = [];
    private long _sequence;

    /// <param name="capacity">Entries kept.</param>
    /// <param name="options">The file formatter's options: the tail redacts the way the file does.</param>
    /// <param name="snapshots">Resolves the host that owned a request; null leaves every request unmatched.</param>
    public AccessLogTail(int capacity, LiveOptions<AccessLogFormatOptions> options, SnapshotStore? snapshots = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, MaximumCapacity);
        ArgumentNullException.ThrowIfNull(options);
        _entries = new AccessLogEntry[capacity];
        _options = options;
        _snapshots = snapshots;
    }

    /// <summary>The entries the buffer keeps.</summary>
    public int Capacity => _entries.Length;

    /// <summary>How many entries have been offered since the process started (the last one's <see cref="AccessLogTailEntry.Sequence"/>).</summary>
    public long Offered
    {
        get
        {
            lock (_gate)
            {
                return _sequence;
            }
        }
    }

    public int SubscriberCount => Volatile.Read(ref _subscribers).Length;

    /// <summary>
    /// Takes a copy of <paramref name="entry"/> into the buffer and hands it to every subscriber it matches. Called by the
    /// drain, one thread; never waits for a reader.
    /// </summary>
    public void Offer(in AccessLogEntry entry)
    {
        var bounded = AccessLogTailEntry.Bound(in entry);
        // Converted before the lock when someone is listening, so the lock covers only the store and the hand-over.
        var copy = Volatile.Read(ref _subscribers).Length > 0 ? Convert(in bounded) : null;
        lock (_gate)
        {
            var sequence = ++_sequence;
            _entries[(int)((sequence - 1) % _entries.Length)] = bounded;
            var subscribers = _subscribers;
            if (subscribers.Length == 0)
            {
                return;
            }

            // A subscriber that arrived between the check and the lock still gets this entry: it was registered before it.
            copy ??= Convert(in bounded);
            copy.Number(sequence);
            foreach (var subscriber in subscribers)
            {
                if (subscriber.Filter.Matches(copy))
                {
                    subscriber.Write(copy);
                }
            }
        }
    }

    /// <summary>The newest <paramref name="limit"/> entries that match, oldest first.</summary>
    public IReadOnlyList<AccessLogTailEntry> Recent(AccessLogTailFilter filter, int limit)
    {
        ArgumentNullException.ThrowIfNull(filter);
        AccessLogEntry[] raw;
        long newest;
        lock (_gate)
        {
            (raw, newest) = CopyBuffer();
        }

        return Collect(raw, newest, filter, limit);
    }

    /// <summary>
    /// Starts a subscription holding the newest <paramref name="backlog"/> entries that match and then every later one. Null when
    /// <see cref="MaxSubscribers"/> are listening already.
    /// </summary>
    public AccessLogTailSubscription? Subscribe(AccessLogTailFilter filter, int backlog, int channelCapacity = SubscriberCapacity)
    {
        ArgumentNullException.ThrowIfNull(filter);
        AccessLogTailSubscription subscription;
        AccessLogEntry[] raw;
        long newest;
        lock (_gate)
        {
            if (_subscribers.Length >= MaxSubscribers)
            {
                return null;
            }

            // The buffer as it is at the moment the subscriber is registered: the backlog is everything up to here, the channel
            // everything after, so nothing is missed or shown twice however long the backlog takes to build outside the lock.
            (raw, newest) = CopyBuffer();
            subscription = new AccessLogTailSubscription(this, filter, channelCapacity);
            // Copy on write: the drain walks the array it read, a subscriber's own disposal builds another.
            AccessLogTailSubscription[] added = [.. _subscribers, subscription];
            Volatile.Write(ref _subscribers, added);
        }

        subscription.SetBacklog(Collect(raw, newest, filter, backlog));
        return subscription;
    }

    internal void Unsubscribe(AccessLogTailSubscription subscription)
    {
        lock (_gate)
        {
            var current = _subscribers;
            var index = Array.IndexOf(current, subscription);
            if (index < 0)
            {
                return;
            }

            var remaining = new AccessLogTailSubscription[current.Length - 1];
            Array.Copy(current, 0, remaining, 0, index);
            Array.Copy(current, index + 1, remaining, index, remaining.Length - index);
            Volatile.Write(ref _subscribers, remaining);
        }
    }

    /// <summary>
    /// The host's names are resolved against the snapshot that is live now, so a backlog entry for a host deleted since reads as
    /// "no host matched"; the entries streamed after a subscription are resolved as the drain offers them.
    /// </summary>
    private AccessLogTailEntry Convert(in AccessLogEntry raw)
    {
        var names = _snapshots?.Current.Hosts.Find(raw.Host) is { } host ? host.Names : null;
        return AccessLogTailEntry.From(in raw, _options.Current, names);
    }

    /// <summary>Under the lock: the buffer's entries oldest first, and the sequence of the newest. Plain struct copies, no conversion.</summary>
    private (AccessLogEntry[] Raw, long Newest) CopyBuffer()
    {
        var newest = _sequence;
        var count = (int)Math.Min(newest, _entries.Length);
        var raw = new AccessLogEntry[count];
        var start = (int)((newest - count) % _entries.Length);
        var first = Math.Min(count, _entries.Length - start);
        Array.Copy(_entries, start, raw, 0, first);
        Array.Copy(_entries, 0, raw, first, count - first);
        return (raw, newest);
    }

    /// <summary>Outside the lock: converts from the newest back and keeps up to <paramref name="limit"/> matches, oldest first.</summary>
    private List<AccessLogTailEntry> Collect(AccessLogEntry[] raw, long newest, AccessLogTailFilter filter, int limit)
    {
        List<AccessLogTailEntry> found = [];
        for (var i = raw.Length - 1; i >= 0 && found.Count < limit; i--)
        {
            var entry = Convert(in raw[i]);
            entry.Number(newest - (raw.Length - 1 - i));
            if (filter.Matches(entry))
            {
                found.Add(entry);
            }
        }

        found.Reverse();
        return found;
    }
}

/// <summary>
/// One listener of <see cref="AccessLogTail"/>: the backlog that matched when it started, and a bounded channel of what came
/// after. A reader that falls behind loses the oldest entries and is told how many (<see cref="TakeSkipped"/>).
/// </summary>
public sealed class AccessLogTailSubscription : IDisposable
{
    private readonly AccessLogTail _hub;
    private readonly Channel<AccessLogTailEntry> _channel;
    private long _skipped;
    private int _disposed;

    internal AccessLogTailSubscription(AccessLogTail hub, AccessLogTailFilter filter, int channelCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channelCapacity, 1);
        _hub = hub;
        Filter = filter;
        _channel = Channel.CreateBounded<AccessLogTailEntry>(
            new BoundedChannelOptions(channelCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true },
            _ => Interlocked.Increment(ref _skipped));
    }

    public AccessLogTailFilter Filter { get; }

    /// <summary>The newest entries that matched when the subscription began, oldest first.</summary>
    public IReadOnlyList<AccessLogTailEntry> Backlog { get; private set; } = [];

    internal void SetBacklog(IReadOnlyList<AccessLogTailEntry> backlog) => Backlog = backlog;

    /// <summary>What came after the backlog; completes when the subscription is disposed.</summary>
    public ChannelReader<AccessLogTailEntry> Reader => _channel.Reader;

    /// <summary>Entries dropped so far because the reader was behind.</summary>
    public long Skipped => Interlocked.Read(ref _skipped);

    /// <summary>The entries dropped since the last call.</summary>
    public long TakeSkipped() => Interlocked.Exchange(ref _skipped, 0);

    internal void Write(AccessLogTailEntry entry) => _channel.Writer.TryWrite(entry);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _hub.Unsubscribe(this);
            _channel.Writer.TryComplete();
        }
    }
}
