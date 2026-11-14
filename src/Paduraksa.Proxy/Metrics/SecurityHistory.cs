using Paduraksa.Core.Model;

namespace Paduraksa.Proxy.Metrics;

/// <summary>
/// The security minutes of the hour before the process started, read from the rollups once at startup: the counterpart of
/// <see cref="MetricHistory"/>, and never reread for the same reason (the boot minute isn't counted twice when its live half is merged into its row).
/// </summary>
internal sealed class SecurityHistory
{
    private readonly Lock _gate = new();
    private Dictionary<(SecurityRollupSubject Subject, Guid Id), SecurityPoint[]> _series = [];

    public void Seed(IEnumerable<SecurityRollup> rollups)
    {
        Dictionary<(SecurityRollupSubject, Guid), List<SecurityPoint>> grouped = [];
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

        Dictionary<(SecurityRollupSubject, Guid), SecurityPoint[]> sorted = [];
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

    /// <summary>Every object of <paramref name="subject"/> and its minutes that end after <paramref name="sinceUnixSeconds"/>.</summary>
    public Dictionary<Guid, SecurityPoint[]> Since(SecurityRollupSubject subject, long sinceUnixSeconds)
    {
        Dictionary<Guid, SecurityPoint[]> taken = [];
        lock (_gate)
        {
            foreach (var ((kind, id), points) in _series)
            {
                if (kind == subject && After(points, sinceUnixSeconds) is { Length: > 0 } inWindow)
                {
                    taken[id] = inWindow;
                }
            }
        }

        return taken;
    }

    /// <summary>Forgets minutes that ended before <paramref name="beforeUnixSeconds"/>.</summary>
    public void Trim(long beforeUnixSeconds)
    {
        lock (_gate)
        {
            if (_series.Count == 0)
            {
                return;
            }

            Dictionary<(SecurityRollupSubject, Guid), SecurityPoint[]> kept = [];
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

    private static SecurityPoint[] After(SecurityPoint[] points, long unixSeconds)
    {
        List<SecurityPoint> kept = [];
        foreach (var point in points)
        {
            if (point.Start + point.Width > unixSeconds)
            {
                kept.Add(point);
            }
        }

        return [.. kept];
    }

    internal static bool TryOwner(SecurityRollupSubject subject, string subjectId, out Guid owner)
    {
        owner = Guid.Empty;
        switch (subject)
        {
            case SecurityRollupSubject.Total when subjectId.Length == 0:
                return true;
            case SecurityRollupSubject.AuthProvider when AuthProviderId.TryParse(subjectId, null, out var provider):
                owner = provider.Value;
                return true;
            case SecurityRollupSubject.RuleSet when RuleSetId.TryParse(subjectId, null, out var set):
                owner = set.Value;
                return true;
            default:
                return false;
        }
    }
}
