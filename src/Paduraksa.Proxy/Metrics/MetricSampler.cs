using System.Collections.Concurrent;

using Paduraksa.Core.Model;

namespace Paduraksa.Proxy.Metrics;

/// <summary>
/// Samples the registry once a second into in-memory rings and finished minutes (docs/design/observability.md "Metrics (M9)"). Each tick reads every host's and
/// upstream's cumulative counters and histograms, takes what each gained since the tick before, and files it in the ten-second
/// buckets and the minute the interval since the last tick belongs to (cut in proportion where it crosses a boundary); a finished
/// bucket goes into the series' ring (an hour of them), a finished minute into the <see cref="MetricRollupBacklog"/> for the
/// rollup job. A totals series sums every host. Every window also records the seconds the sampler observed.
/// </summary>
/// <remarks>
/// <para><b>Series follow objects, not slots.</b> A series is keyed by the slot's owner. When a slot is rented to another object
/// the old series is flushed (its unfinished minute goes to the queue under its own typed ID) and the new owner starts
/// empty. A retired slot is kept a grace period of many ticks before reuse, so the sampler has normally seen all its counts;
/// a count that lands after the clear is lost, and a tick that overlaps a rent is skipped (see <see cref="Observe"/>).</para>
/// <para><b>Memory is fixed by the slots.</b> A series holds an hour of 160-byte points (about 58 KB) and two windows; hosts and
/// upstreams have series, routes and destinations only the counters. A series is created the first time its slot is seen in use. The seed adds the
/// hour before the start as minute points (a few thousand at most), forgotten as they leave the hour.</para>
/// <para><b>The unmatched host</b> (requests no host owns) is a host series with an empty ID, and part of the totals.</para>
/// <para><b>Threads.</b> <see cref="Tick"/> is called by one thread. Readers get copies taken under the series' own lock, never
/// the sampler's arrays.</para>
/// </remarks>
public sealed class MetricSampler
{
    private readonly MetricsRegistry _registry;
    private readonly TimeProvider _time;
    private readonly List<MetricSeries?> _hosts = [];
    private readonly List<MetricSeries?> _upstreams = [];
    private readonly ConcurrentDictionary<(MetricRollupSubject Subject, Guid Id), MetricSeries> _byOwner = new();
    private readonly MetricSeries _total = new(MetricRollupSubject.Total, Guid.Empty, 0, string.Empty);
    private readonly long[] _counters = new long[RequestMetric.Stride];
    private readonly long[] _totalHistogram = new long[LatencyBuckets.Stride];
    private readonly long[] _upstreamHistogram = new long[LatencyBuckets.Stride];
    private readonly long[] _sumCounters = new long[RequestMetric.Stride];
    private readonly long[] _sumTotal = new long[LatencyBuckets.Stride];
    private readonly long[] _sumUpstream = new long[LatencyBuckets.Stride];
    private readonly MetricHistory _history = new();
    private readonly List<MetricSeries> _observed = [];
    private long _lastMillis;
    private long _lastStamp;
    private long _lastMinute = -1;

    public MetricSampler(MetricsRegistry registry, TimeProvider time, MetricRollupBacklog rollups)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(rollups);
        _registry = registry;
        _time = time;
        Rollups = rollups;
        // The registry has counted since it was made: the first tick covers the time since the sampler was.
        _lastMillis = time.GetUtcNow().ToUnixTimeMilliseconds();
        _lastStamp = time.GetTimestamp();
    }

    /// <summary>
    /// A gap between two ticks longer than this in which nothing was counted is an outage or a suspension, and is not counted as observed.
    /// One in which requests were counted means the process was running and serving and only the sampler was late: it is spread over
    /// the whole gap like any tick (up to the ring's hour).
    /// </summary>
    public static readonly TimeSpan MaxTickGap = TimeSpan.FromSeconds(5);

    /// <summary>The queue the finished minutes go to.</summary>
    public MetricRollupBacklog Rollups { get; }

    /// <summary>The ring's reach in seconds: an hour.</summary>
    public static int RingSeconds => MetricSeries.RingPoints * MetricSeries.BucketSeconds;

    /// <summary>
    /// Reads the registry once and files what it gained since the last call over the time it was observed, split in proportion where
    /// the interval crosses a ten-second or a minute boundary and with the counts kept whole. One thread at a time.
    /// <para>The length of the interval, and so the coverage, is monotonic time (<see cref="TimeProvider.GetTimestamp"/>); the wall
    /// clock only says which bucket and minute a moment is in. It never goes back: a wall clock stepped backwards (an NTP
    /// correction) leaves the labels running on at the monotonic rate until it catches up, so a bucket or a minute already closed is
    /// never opened again, a minute is never written twice, and each real second is counted once.</para>
    /// </summary>
    public void Tick()
    {
        var wall = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var stamp = _time.GetTimestamp();
        var elapsed = Math.Max(0L, (long)_time.GetElapsedTime(_lastStamp, stamp).TotalMilliseconds);
        _lastStamp = stamp;
        var end = wall >= _lastMillis ? wall : _lastMillis + elapsed;
        var start = Math.Max(_lastMillis, end - elapsed);
        _lastMillis = end;
        if (end / 60_000 != _lastMinute)
        {
            _lastMinute = end / 60_000;
            _history.Trim((end / 1000) - RingSeconds - 60);
        }

        Array.Clear(_sumCounters);
        Array.Clear(_sumTotal);
        Array.Clear(_sumUpstream);
        _observed.Clear();

        for (var slot = 0; slot < _registry.Hosts.HighWater; slot++)
        {
            if (Observe(_hosts, MetricRollupSubject.Host, MetricSubject.Host, _registry.Hosts, slot) is not { } series)
            {
                continue;
            }

            series.Sample(_counters, _totalHistogram, _upstreamHistogram);
            _observed.Add(series);

            // The totals are the sum of the hosts' gains, not a read of their own: a host that is deleted keeps what it already counted.
            Add(_sumCounters, series.DeltaCounters);
            Add(_sumTotal, series.DeltaTotal);
            Add(_sumUpstream, series.DeltaUpstream);
        }

        for (var slot = 0; slot < _registry.Upstreams.HighWater; slot++)
        {
            if (Observe(_upstreams, MetricRollupSubject.Upstream, MetricSubject.Upstream, _registry.Upstreams, slot) is { } series)
            {
                series.Sample(_counters, _totalHistogram, _upstreamHistogram);
                _observed.Add(series);
            }
        }

        // A long gap with nothing counted in it is time nobody observed; one with requests in it was lived through.
        var gained = _sumCounters.AsSpan().ContainsAnyExcept(0L);
        var split = elapsed > MaxTickGap.TotalMilliseconds && !gained
            ? TickSplit.None(end, MetricSeries.BucketSeconds * 1000L)
            : TickSplit.Of(Math.Max(start, end - (RingSeconds * 1000L)), end, MetricSeries.BucketSeconds * 1000L);
        foreach (var series in _observed)
        {
            series.Absorb(split, Rollups);
        }

        _total.Absorb(split, _sumCounters, _sumTotal, _sumUpstream, Rollups);
    }

    /// <summary>
    /// A last reading, then every unfinished bucket and minute is closed as it stands, partial, with the seconds it observed, and the
    /// minutes are queued for the rollup job. For a process that is stopping: what it saw of its last minute isn't lost with it.
    /// </summary>
    public void Flush()
    {
        Tick();
        _total.Flush(Rollups);
        foreach (var series in _byOwner.Values)
        {
            series.Flush(Rollups);
        }
    }

    /// <summary>The ring of one object (<see cref="Guid.Empty"/> for the totals, and for the unmatched host), or null when nothing has been sampled for it.</summary>
    public MetricSeriesView? Read(MetricRollupSubject subject, Guid id)
    {
        var series = subject == MetricRollupSubject.Total ? _total : _byOwner.GetValueOrDefault((subject, id));
        return series?.View();
    }

    /// <summary>
    /// What every object of <paramref name="subject"/> did over the last <paramref name="window"/> (at most the ring's hour), one point
    /// each, keyed by the object's owner GUID; the unmatched host is under <see cref="Guid.Empty"/>. It reaches back before the
    /// process started as far as the history it was seeded with does.
    /// </summary>
    public Dictionary<Guid, MetricPoint> Summarise(MetricRollupSubject subject, TimeSpan window)
    {
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var since = now - (long)Math.Min(window.TotalSeconds, RingSeconds);
        Dictionary<Guid, MetricPoint> summaries = [];
        foreach (var ((kind, id), series) in _byOwner)
        {
            if (kind == subject)
            {
                summaries[id] = series.Summarise(since, now);
            }
        }

        foreach (var (id, minutes) in _history.Since(subject, since))
        {
            summaries[id] = summaries.TryGetValue(id, out var live)
                ? MetricPoints.Combine([live, .. minutes], Math.Min(since, minutes[0].Start), (int)(now - since))
                : MetricPoints.Combine(minutes, minutes[0].Start, (int)(now - since));
        }

        return summaries;
    }

    /// <summary>The whole process over the last <paramref name="window"/> (at most the ring's hour), from the ring alone: it is the pace now, not the hour's history.</summary>
    public MetricPoint SummariseTotal(TimeSpan window)
    {
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        return _total.Summarise(now - (long)Math.Min(window.TotalSeconds, RingSeconds), now);
    }

    /// <summary>The minutes before the process started that the sampler was seeded with, oldest first; empty for none.</summary>
    public MetricPoint[] History(MetricRollupSubject subject, Guid id) => _history.Of(subject, id);

    /// <summary>
    /// Gives the sampler the rollup minutes of the hour before it started. Called once, before the first tick, by whatever reads the
    /// database at startup; the sampler itself never touches it.
    /// </summary>
    public void Seed(IEnumerable<MetricRollup> history) => _history.Seed(history);

    /// <summary>
    /// Reads one slot into the sampler's buffers and returns its series, or null when there is nothing to file this tick. The
    /// slot's version is read before the owner and the counters and again after: a slot rented in between (the publish thread
    /// clears its counters and changes its owner) is skipped for this tick, so a half-cleared read never becomes a gain, and
    /// the next tick finds the new owner under a new version.
    /// </summary>
    private MetricSeries? Observe(List<MetricSeries?> bySlot, MetricRollupSubject kind, MetricSubject subject, MetricSlotRegistry registry, int slot)
    {
        var version = registry.VersionOf(slot);
        if ((version & 1) != 0)
        {
            return null;
        }

        var owner = registry.OwnerOf(slot);
        var live = registry.IsLive(slot);
        _registry.ReadCounters(subject, slot, _counters);
        _registry.ReadLatency(subject, slot, _totalHistogram, _upstreamHistogram);
        return registry.VersionOf(slot) != version ? null : SeriesFor(bySlot, kind, owner, live, version, slot);
    }

    private MetricSeries? SeriesFor(List<MetricSeries?> bySlot, MetricRollupSubject kind, Guid owner, bool live, int version, int slot)
    {
        while (bySlot.Count <= slot)
        {
            bySlot.Add(null);
        }

        var existing = bySlot[slot];
        if (existing is not null && existing.Owner == owner && existing.Version == version)
        {
            return existing;
        }

        if (existing is not null)
        {
            // The slot went to another object: what the old one had counted in its last minute goes out under its own ID.
            existing.Flush(Rollups);
            _byOwner.TryRemove((kind, existing.Owner), out _);
            bySlot[slot] = null;
        }

        // A slot that was never handed out has no owner and isn't live; the reserved unmatched slot has no owner and is.
        if (owner == Guid.Empty && !live)
        {
            return null;
        }

        MetricSeries created = new(kind, owner, version, IdText(kind, owner));
        bySlot[slot] = created;
        _byOwner[(kind, owner)] = created;
        return created;
    }

    private static string IdText(MetricRollupSubject kind, Guid owner) => owner == Guid.Empty ? string.Empty : kind switch
    {
        MetricRollupSubject.Host => HostId.FromValue(owner).ToString(),
        MetricRollupSubject.Upstream => UpstreamId.FromValue(owner).ToString(),
        _ => string.Empty,
    };

    private static void Add(long[] sum, ReadOnlySpan<long> gained)
    {
        for (var i = 0; i < sum.Length && i < gained.Length; i++)
        {
            sum[i] += gained[i];
        }
    }
}
