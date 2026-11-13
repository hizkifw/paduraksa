using System.Buffers.Binary;
using System.Security.Cryptography;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Proxy.Analytics;

/// <summary>
/// The traffic analytics (PLAN §7.4, analytics): the third consumer of the access-log drain, beside the file and the live tail. The
/// request path knows nothing of it. The drain offers each entry here after the file has its line, in a <c>try</c> of its own, so a
/// failure here never costs the file a line; an entry of a host whose access log is off never reached the ring, so it is in neither.
/// </summary>
/// <remarks>
/// <para><b>Buckets.</b> Five minutes wide, one <see cref="TrafficBucket"/> per host (and one for the requests no host owns) filled
/// in memory. A bucket closes when its time has passed (<see cref="CloseGrace"/> later, for entries still in the ring) and its rollups
/// go to the <see cref="AnalyticsBacklog"/>, which the persisting job drains. <see cref="Flush"/> closes what is open, as the process
/// stops. <b>Memory</b> is the open buckets, normally one or two, times the hosts that had traffic (at most
/// <see cref="MaxHosts"/> of them): about 4 KiB of visitor registers, the four sketches (at most 64, 32, 32 and 16 keys) and up to 64
/// latency histograms of 808 bytes each, so around 100 KiB for a busy host and a few KiB for a quiet one.</para>
/// <para><b>Coverage.</b> Every tick adds the time it observed to the buckets it falls in, so a bucket knows how many of its seconds the
/// drain was running for, and a bucket nobody observed is a gap in the chart and not zeros (the same rule as the metrics timeline). A
/// tick later than <see cref="MaxTickGap"/> counts as observed only when it brought entries: a suspended process has nothing in its
/// ring.</para>
/// <para><b>Visitors.</b> The client address is never stored. It is hashed with a random per-process salt that lives in memory only
/// and goes into a <see cref="HyperLogLog"/>, which keeps only the longest run of zeros per register, so what reaches the
/// database can't be turned back into an address, even by someone who holds the database and the binary. The cost of a salt that dies
/// with the process: a visitor active across a restart is counted once on each side of it. An IPv6 address counts by its /64, since a
/// device rotates the rest.</para>
/// <para><b>Threads.</b> The drain writes; the dashboard reads the open buckets through <see cref="Live"/>. One lock covers both, held
/// for a record or a copy, never by the request path.</para>
/// </remarks>
public sealed class TrafficAnalytics
{
    /// <summary>The width of a bucket.</summary>
    public const int BucketSeconds = 300;

    /// <summary>How long after its end a bucket stays open for entries the drain hasn't reached.</summary>
    public static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(5);

    /// <summary>A tick further apart than this counts as time observed only when it brought entries.</summary>
    public static readonly TimeSpan MaxTickGap = TimeSpan.FromSeconds(5);

    /// <summary>Hosts with their own table in one bucket; past it a host's traffic is counted with the requests no host owns.</summary>
    public const int MaxHosts = 1_024;

    private const int SaltLength = 16;
    private const int MaxOpenBuckets = 12;
    private const int MaxRobotsCache = 2_048;

    private static readonly long UnixEpochTicks = DateTime.UnixEpoch.Ticks;

    private readonly SnapshotStore _snapshots;
    private readonly TimeProvider _time;
    private readonly AnalyticsBacklog _backlog;
    private readonly byte[] _salt;
    private readonly UserAgentCache _agents = new();
    private readonly Lock _gate = new();
    private readonly SortedDictionary<long, OpenBucket> _open = [];
    private readonly Dictionary<HostId, RobotsEntry> _robots = [];
    private long _lastTick;
    private long _closedBefore;
    private bool _sawEntries;

    /// <param name="snapshots">Resolves the host an entry was for, and its <c>robots.txt</c>, from the live snapshot.</param>
    /// <param name="time">The clock the buckets' edges and coverage come from.</param>
    /// <param name="backlog">Where closed buckets go.</param>
    /// <param name="salt">The visitor hash's salt; null makes a random one, which is what the process wants. A test passes one to be repeatable.</param>
    public TrafficAnalytics(SnapshotStore snapshots, TimeProvider time, AnalyticsBacklog backlog, byte[]? salt = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(backlog);
        _snapshots = snapshots;
        _time = time;
        _backlog = backlog;
        if (salt is not null && salt.Length != SaltLength)
        {
            throw new ArgumentException($"The salt is {SaltLength} bytes.", nameof(salt));
        }

        _salt = salt ?? RandomNumberGenerator.GetBytes(SaltLength);
        _lastTick = time.GetTimestamp();
    }

    /// <summary>Entries counted since the process started.</summary>
    public long Counted { get; private set; }

    /// <summary>
    /// Counts one finished request. Called by the drain, one thread. <paramref name="behind"/> says the drain is far behind the ring: the entry is
    /// still counted, but an agent that is not already known is not classified (it is <see cref="UserAgentInfo.Unclassified"/>) and the
    /// robots check is skipped, so the analytics never take time the file's lines need.
    /// </summary>
    public void Offer(in AccessLogEntry entry, bool behind = false)
    {
        var snapshot = _snapshots.Current;
        var host = snapshot.Hosts.Find(entry.Host);
        var visitor = VisitorHash(entry.ClientAddress);
        var agent = _agents.Get(entry.UserAgent, classify: !behind);
        var ignored = !behind && host is not null && IgnoredRobots(host, in entry, agent);
        var started = Align((entry.TimestampUtcTicks - UnixEpochTicks) / TimeSpan.TicksPerSecond);
        lock (_gate)
        {
            _sawEntries = true;
            Counted++;
            var bucket = BucketFor(started, host is null ? default : host.Id);
            bucket.Record(in entry, visitor, agent, ignored);
        }
    }

    /// <summary>
    /// Credits the time since the last tick to the buckets it fell in, and closes the buckets whose time is up. The drain calls it on every
    /// pass, so a quiet bucket is still observed.
    /// </summary>
    public void Tick()
    {
        var nowTimestamp = _time.GetTimestamp();
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        List<AnalyticsRollup> closed;
        lock (_gate)
        {
            Observe(nowTimestamp, now);
            closed = CloseDue(now - (long)CloseGrace.TotalSeconds);
        }

        if (closed.Count > 0)
        {
            _backlog.Add(closed);
        }
    }

    /// <summary>Closes everything that is open, partial buckets included, as the process stops.</summary>
    public void Flush()
    {
        var nowTimestamp = _time.GetTimestamp();
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        List<AnalyticsRollup> closed;
        lock (_gate)
        {
            Observe(nowTimestamp, now);
            closed = CloseDue(long.MaxValue);
        }

        if (closed.Count > 0)
        {
            _backlog.Add(closed);
        }
    }

    /// <summary>
    /// What the open buckets hold right now, as copies the reader may keep: the part of the range no row has yet. A host that has a
    /// bucket open but no traffic in it is absent; coverage is present as a row of its own. <paramref name="only"/> keeps the copies to one
    /// host (<paramref name="host"/>) or to the requests no host owns; null copies every host's.
    /// </summary>
    public IReadOnlyList<LiveBucket> Live(AnalyticsSubject? only = null, HostId host = default)
    {
        lock (_gate)
        {
            List<LiveBucket> live = [];
            foreach (var (start, open) in _open)
            {
                live.Add(new LiveBucket(AnalyticsSubject.Coverage, default, start, (int)open.Covered, null));
                foreach (var (owner, bucket) in open.Hosts)
                {
                    var subject = owner.IsEmpty ? AnalyticsSubject.Unmatched : AnalyticsSubject.Host;
                    if (only is null || (only == subject && (subject != AnalyticsSubject.Host || owner == host)))
                    {
                        live.Add(new LiveBucket(subject, owner, start, (int)open.Covered, bucket.Clone()));
                    }
                }
            }

            return live;
        }
    }

    private static long Align(long seconds) => seconds - (((seconds % BucketSeconds) + BucketSeconds) % BucketSeconds);

    // Under the lock. The entry's own bucket if it is still open; one that closed already (a drain that ran late) goes to the oldest open one.
    private TrafficBucket BucketFor(long started, HostId host)
    {
        if (started < _closedBefore)
        {
            // Dropping the entry would lose a request that was served; the oldest open bucket is the nearest stretch that still counts.
            started = OldestOpen() ?? _closedBefore;
        }

        if (!_open.TryGetValue(started, out var open))
        {
            open = new OpenBucket();
            _open[started] = open;
            while (_open.Count > MaxOpenBuckets)
            {
                // A clock that jumps about must not pile buckets up: the oldest closes as it is.
                var oldest = OldestOpen()!.Value;
                _backlog.Add(Close(oldest, _open[oldest]));
                _closedBefore = Math.Max(_closedBefore, oldest + BucketSeconds);
                _open.Remove(oldest);
            }
        }

        if (!open.Hosts.TryGetValue(host, out var bucket))
        {
            if (open.Hosts.Count >= MaxHosts && !host.IsEmpty)
            {
                host = default;
            }

            if (!open.Hosts.TryGetValue(host, out bucket))
            {
                bucket = new TrafficBucket();
                open.Hosts[host] = bucket;
            }
        }

        return bucket;
    }

    private long? OldestOpen()
    {
        foreach (var start in _open.Keys)
        {
            return start;
        }

        return null;
    }

    // Under the lock.
    private void Observe(long nowTimestamp, long now)
    {
        var elapsed = _time.GetElapsedTime(_lastTick, nowTimestamp);
        _lastTick = nowTimestamp;
        var brought = _sawEntries;
        _sawEntries = false;
        if (elapsed <= TimeSpan.Zero || (elapsed > MaxTickGap && !brought))
        {
            // Nothing was watching for a stretch that long: it stays unobserved. The bucket of this moment still exists.
            EnsureOpen(Align(now));
            return;
        }

        var end = now;
        var span = Math.Min(elapsed.TotalSeconds, MaxOpenBuckets * BucketSeconds);
        var begin = end - span;
        for (var start = Align((long)Math.Floor(begin)); start <= Align(end); start += BucketSeconds)
        {
            var overlap = Math.Min(end, start + BucketSeconds) - Math.Max(begin, start);
            if (overlap > 0 && start >= _closedBefore)
            {
                var open = EnsureOpen(start);
                open.Covered = Math.Min(BucketSeconds, open.Covered + overlap);
            }
        }

        EnsureOpen(Align(now));
    }

    private OpenBucket EnsureOpen(long start)
    {
        if (!_open.TryGetValue(start, out var open))
        {
            open = new OpenBucket();
            _open[start] = open;
        }

        return open;
    }

    // Under the lock: closes every bucket whose end is at or before the horizon.
    private List<AnalyticsRollup> CloseDue(long horizon)
    {
        List<AnalyticsRollup> closed = [];
        foreach (var start in new List<long>(_open.Keys))
        {
            if (horizon == long.MaxValue || start + BucketSeconds <= horizon)
            {
                closed.AddRange(Close(start, _open[start]));
                _closedBefore = Math.Max(_closedBefore, start + BucketSeconds);
                _open.Remove(start);
            }
        }

        return closed;
    }

    private static List<AnalyticsRollup> Close(long start, OpenBucket open)
    {
        List<AnalyticsRollup> rollups = [];
        var covered = (int)Math.Round(open.Covered);
        if (covered > 0)
        {
            rollups.Add(new AnalyticsRollup(AnalyticsSubject.Coverage, string.Empty, start, covered, 0, 0, 0, 0, null));
        }

        foreach (var (host, bucket) in open.Hosts)
        {
            if (bucket.Requests == 0)
            {
                continue;
            }

            rollups.Add(new AnalyticsRollup(
                host.IsEmpty ? AnalyticsSubject.Unmatched : AnalyticsSubject.Host,
                host.IsEmpty ? string.Empty : host.ToString(),
                start,
                covered,
                bucket.Requests,
                bucket.BytesOut,
                bucket.BytesIn,
                bucket.ServerErrors,
                TrafficBucketCodec.Encode(bucket)));
        }

        return rollups;
    }

    // Client addresses are hashed with the process's salt; IPv4-mapped IPv6 counts as the IPv4 address and an IPv6 address as its /64. The
    // parser is the gate's own, which allocates nothing.
    private ulong VisitorHash(string? address)
    {
        if (string.IsNullOrEmpty(address) || !IpParser.TryParse(address, out var key))
        {
            return 0;
        }

        Span<byte> input = stackalloc byte[SaltLength + 8];
        _salt.CopyTo(input);
        int length;
        if (key.IsIPv4)
        {
            BinaryPrimitives.WriteUInt32BigEndian(input[SaltLength..], key.IPv4Value);
            length = SaltLength + 4;
        }
        else
        {
            BinaryPrimitives.WriteUInt64BigEndian(input[SaltLength..], (ulong)(key.Value >> 64));
            length = SaltLength + 8;
        }

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input[..length], hash);
        var value = BinaryPrimitives.ReadUInt64BigEndian(hash);
        return value == 0 ? 1 : value;
    }

    // A bot of a kind that is supposed to read robots.txt, fetching what the host's file disallows for it. Bounded work for any entry: the
    // path is cut at the key cap and the group that applies to an agent is resolved once and kept (a flood of unique agents resolves each
    // once, which is a scan of the file's few groups, and the rules are matched against at most 256 characters).
    private bool IgnoredRobots(CompiledHost host, in AccessLogEntry entry, UserAgentInfo agent)
    {
        if (host.RobotsTxt is not { } source || agent.Bot is BotClass.None or BotClass.Monitor or BotClass.Library || entry.UserAgent is null)
        {
            return false;
        }

        // Parsed once per host and snapshot: a publish compiles a new response object, which is what invalidates the entry.
        if (!_robots.TryGetValue(host.Id, out var cached) || !ReferenceEquals(cached.Source, source))
        {
            if (_robots.Count >= MaxRobotsCache)
            {
                _robots.Clear();
            }

            cached = new RobotsEntry(source, RobotsRules.FromBody(source.Body));
            _robots[host.Id] = cached;
        }

        var path = (entry.Path ?? "/").AsSpan();
        return !cached.For(entry.UserAgent).IsAllowed(path.Length > TrafficBucket.MaxKeyLength ? path[..TrafficBucket.MaxKeyLength] : path);
    }

    private sealed class RobotsEntry(PrecomputedResponse source, RobotsRules rules)
    {
        private const int MaxResolved = 256;

        private readonly Dictionary<string, RobotsRules.Applicable> _resolved = new(StringComparer.Ordinal);

        public PrecomputedResponse Source { get; } = source;

        public RobotsRules.Applicable For(string userAgent)
        {
            var capped = userAgent.Length > TrafficBucket.MaxKeyLength ? userAgent[..TrafficBucket.MaxKeyLength] : userAgent;
            if (!_resolved.TryGetValue(capped, out var applicable))
            {
                if (_resolved.Count >= MaxResolved)
                {
                    _resolved.Clear();
                }

                applicable = rules.Resolve(capped);
                _resolved[capped] = applicable;
            }

            return applicable;
        }
    }

    private sealed class OpenBucket
    {
        public double Covered { get; set; }

        public Dictionary<HostId, TrafficBucket> Hosts { get; } = [];
    }
}

/// <summary>A copy of an open bucket, for a reader.</summary>
/// <param name="Subject">What it is about.</param>
/// <param name="Host">The host, empty for coverage and the requests no host owns.</param>
/// <param name="Start">The bucket's first second.</param>
/// <param name="CoveredSeconds">Seconds observed so far.</param>
/// <param name="Bucket">The traffic, or null for coverage.</param>
public sealed record LiveBucket(AnalyticsSubject Subject, HostId Host, long Start, int CoveredSeconds, TrafficBucket? Bucket);
