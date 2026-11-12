using Paduraksa.Core.Model;

namespace Paduraksa.Proxy.Metrics;

/// <summary>
/// The minutes of the hour before the process started, read from the rollups at startup (PLAN §7.4): what lets the one-hour view
/// and its tables reach back past the boot. It is a snapshot taken before the first tick, never reread, so the minute the process
/// booted in is not counted twice when the rollup job later merges the live half into its row.
/// </summary>
internal sealed class MetricHistory
{
    private readonly Lock _gate = new();
    private Dictionary<(MetricRollupSubject Subject, Guid Id), MetricPoint[]> _series = [];

    /// <summary>Replaces what is held with the minutes of <paramref name="rollups"/>, grouped by object.</summary>
    public void Seed(IEnumerable<MetricRollup> rollups)
    {
        Dictionary<(MetricRollupSubject, Guid), List<MetricPoint>> grouped = [];
        foreach (var rollup in rollups)
        {
            if (!TryOwner(rollup.Subject, rollup.SubjectId, out var owner))
            {
                continue;
            }

            if (!grouped.TryGetValue((rollup.Subject, owner), out var list))
            {
                grouped[(rollup.Subject, owner)] = list = [];
            }

            list.Add(rollup.Point);
        }

        Dictionary<(MetricRollupSubject, Guid), MetricPoint[]> sorted = [];
        foreach (var (key, list) in grouped)
        {
            var points = list.ToArray();
            Array.Sort(points, static (a, b) => a.Start.CompareTo(b.Start));
            sorted[key] = points;
        }

        lock (_gate)
        {
            _series = sorted;
        }
    }

    /// <summary>The minutes held for one object (the totals: <see cref="Guid.Empty"/>), oldest first.</summary>
    public MetricPoint[] Of(MetricRollupSubject subject, Guid id)
    {
        lock (_gate)
        {
            return _series.GetValueOrDefault((subject, id)) ?? [];
        }
    }

    /// <summary>Every object of <paramref name="subject"/> and its minutes that end after <paramref name="sinceUnixSeconds"/>.</summary>
    public Dictionary<Guid, MetricPoint[]> Since(MetricRollupSubject subject, long sinceUnixSeconds)
    {
        Dictionary<Guid, MetricPoint[]> taken = [];
        lock (_gate)
        {
            foreach (var ((kind, id), points) in _series)
            {
                if (kind != subject)
                {
                    continue;
                }

                var inWindow = After(points, sinceUnixSeconds);
                if (inWindow.Length > 0)
                {
                    taken[id] = inWindow;
                }
            }
        }

        return taken;
    }

    /// <summary>Forgets minutes that ended before <paramref name="beforeUnixSeconds"/>: the hour has moved past them.</summary>
    public void Trim(long beforeUnixSeconds)
    {
        lock (_gate)
        {
            if (_series.Count == 0)
            {
                return;
            }

            Dictionary<(MetricRollupSubject, Guid), MetricPoint[]> kept = [];
            foreach (var (key, points) in _series)
            {
                var recent = After(points, beforeUnixSeconds);
                if (recent.Length > 0)
                {
                    kept[key] = recent;
                }
            }

            _series = kept;
        }
    }

    private static MetricPoint[] After(MetricPoint[] points, long unixSeconds)
    {
        List<MetricPoint> kept = [];
        foreach (var point in points)
        {
            if (point.Start + point.Width > unixSeconds)
            {
                kept.Add(point);
            }
        }

        return [.. kept];
    }

    private static bool TryOwner(MetricRollupSubject subject, string subjectId, out Guid owner)
    {
        owner = Guid.Empty;
        if (subjectId.Length == 0)
        {
            return true;
        }

        switch (subject)
        {
            case MetricRollupSubject.Host when HostId.TryParse(subjectId, null, out var host):
                owner = host.Value;
                return true;
            case MetricRollupSubject.Upstream when UpstreamId.TryParse(subjectId, null, out var upstream):
                owner = upstream.Value;
                return true;
            default:
                return false;
        }
    }
}
