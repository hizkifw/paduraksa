using Paduraksa.Proxy.Metrics;

namespace Paduraksa.Application.Metrics;

/// <summary>
/// Lays a chart's buckets out in time so that a stretch the process wasn't there for is a bucket of its own with no coverage, never a
/// jump from one point to the next (PLAN §7.4). The series runs from the first bucket the process observed to the last; what lies
/// outside it (the part of the range before there was any data, the minute the rollups haven't been written for yet) is not a gap.
/// </summary>
internal static class MetricTimeline
{
    private const int MinuteSeconds = 60;
    private const int HourSeconds = 3_600;

    /// <summary>
    /// The last hour: a bucket a minute from the rollups the sampler was seeded with, up to where this run's ring starts, then the
    /// ring's own ten-second buckets. A pre-boot minute is cut short where the ring begins (the boot minute's first half), and a minute
    /// the history has no row for is a bucket nobody observed. When the ring begins exactly on the boot minute's first second, the
    /// pre-boot half of that minute and the ring's first bucket are the same stretch of time seen by two processes: they are one point.
    /// </summary>
    public static List<MetricPoint> Hour(MetricPoint[] history, MetricSeriesView? ring, DateTimeOffset at)
    {
        var now = at.ToUnixTimeSeconds();
        var windowStart = now - HourSeconds;
        List<MetricPoint> live = [];
        if (ring is { } view)
        {
            live.AddRange(view.Points.Where(point => point.Start + point.Width > windowStart));
            if (view.Open.Width > 0 && (view.Open.CoveredSeconds > 0 || view.Open.Requests > 0))
            {
                live.Add(view.Open);
            }
        }

        var liveStart = live.Count > 0 ? live[0].Start : now;
        var minutes = history.ToDictionary(static point => point.Start);
        List<MetricPoint> points = [];
        for (var minute = windowStart / MinuteSeconds * MinuteSeconds; minute < liveStart; minute += MinuteSeconds)
        {
            var width = (int)Math.Min(MinuteSeconds, liveStart - minute);
            points.Add(minutes.TryGetValue(minute, out var stored)
                ? stored with { Width = width, CoveredSeconds = Math.Min(stored.CoveredSeconds, width) }
                : new MetricPoint { Start = minute, Width = width });
        }

        if (live.Count > 0 && minutes.TryGetValue(liveStart, out var bootHalf))
        {
            var width = live[0].Width;
            var merged = MetricPoints.Combine([bootHalf, live[0]], liveStart, width);
            live[0] = merged with { CoveredSeconds = Math.Min(merged.CoveredSeconds, width) };
        }

        points.AddRange(live);
        return Trim(points);
    }

    /// <summary>
    /// A longer range's steps (one per step that has any rollup rows) laid out on the grid of <paramref name="stepSeconds"/>, the steps with
    /// no row in between present as steps nobody observed.
    /// </summary>
    public static List<MetricPoint> Steps(IReadOnlyList<MetricPoint> steps, int stepSeconds)
    {
        var observed = steps.Where(static step => step.CoveredSeconds > 0).OrderBy(static step => step.Start).ToList();
        if (observed.Count == 0)
        {
            return [];
        }

        var byStart = observed.ToDictionary(static step => step.Start);
        List<MetricPoint> points = [];
        for (var start = observed[0].Start; start <= observed[^1].Start; start += stepSeconds)
        {
            points.Add(byStart.TryGetValue(start, out var step) ? step : new MetricPoint { Start = start, Width = stepSeconds });
        }

        return points;
    }

    private static List<MetricPoint> Trim(List<MetricPoint> points)
    {
        var first = points.FindIndex(static point => point.CoveredSeconds > 0);
        if (first < 0)
        {
            return [];
        }

        var last = points.FindLastIndex(static point => point.CoveredSeconds > 0);
        return points.GetRange(first, last - first + 1);
    }
}
