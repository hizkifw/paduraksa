namespace Paduraksa.Proxy.Metrics;

/// <summary>A point of a rollup series: the span it covers, for the history to tell which are still inside the hour.</summary>
internal interface IRollupPoint
{
    long Start { get; }

    int Width { get; }
}

/// <summary>A stored rollup row: the object it is of and its minute.</summary>
internal interface IRollup<out TSubject, out TPoint>
{
    TSubject Subject { get; }

    string SubjectId { get; }

    TPoint Point { get; }
}

/// <summary>
/// The minutes of the hour before the process started, read from the rollups once at startup: what lets the one-hour view and its tables
/// reach back past the boot. It is a snapshot taken before the first tick and never reread, so the minute the process booted in is not
/// counted twice when the rollup job later merges the live half into its row.
/// </summary>
/// <typeparam name="TSubject">What a series is of (hosts, upstreams, the totals...).</typeparam>
/// <typeparam name="TPoint">A class or a struct: a struct point is never boxed, since every use is constrained.</typeparam>
internal abstract class RollupHistory<TSubject, TPoint>
    where TSubject : struct, Enum
    where TPoint : IRollupPoint
{
    private readonly Lock _gate = new();
    private Dictionary<(TSubject Subject, Guid Id), TPoint[]> _series = [];

    /// <summary>Replaces what is held with the minutes of <paramref name="rollups"/>, grouped by object.</summary>
    public void Seed<TRollup>(IEnumerable<TRollup> rollups)
        where TRollup : IRollup<TSubject, TPoint>
    {
        Dictionary<(TSubject, Guid), List<TPoint>> grouped = [];
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

        Dictionary<(TSubject, Guid), TPoint[]> sorted = [];
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
    public TPoint[] Of(TSubject subject, Guid id)
    {
        lock (_gate)
        {
            return _series.GetValueOrDefault((subject, id)) ?? [];
        }
    }

    /// <summary>Every object of <paramref name="subject"/> and its minutes that end after <paramref name="sinceUnixSeconds"/>.</summary>
    public Dictionary<Guid, TPoint[]> Since(TSubject subject, long sinceUnixSeconds)
    {
        Dictionary<Guid, TPoint[]> taken = [];
        lock (_gate)
        {
            foreach (var ((kind, id), points) in _series)
            {
                if (EqualityComparer<TSubject>.Default.Equals(kind, subject) && After(points, sinceUnixSeconds) is { Length: > 0 } inWindow)
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

            Dictionary<(TSubject, Guid), TPoint[]> kept = [];
            foreach (var (key, points) in _series)
            {
                if (After(points, beforeUnixSeconds) is { Length: > 0 } recent)
                {
                    kept[key] = recent;
                }
            }

            _series = kept;
        }
    }

    /// <summary>The object a rollup row is of (<see cref="Guid.Empty"/> for the totals); false for a row this history doesn't keep.</summary>
    protected abstract bool TryOwner(TSubject subject, string subjectId, out Guid owner);

    private static TPoint[] After(TPoint[] points, long unixSeconds)
    {
        List<TPoint> kept = [];
        foreach (var point in points)
        {
            if (point.Start + point.Width > unixSeconds)
            {
                kept.Add(point);
            }
        }

        return [.. kept];
    }
}
