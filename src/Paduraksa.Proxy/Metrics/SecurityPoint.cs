using Paduraksa.Core.Model;

namespace Paduraksa.Proxy.Metrics;

/// <summary>
/// What the gate's security counters gained over one stretch of time: a minute the sampler closed, a stored rollup minute, or a range
/// summed from them; and how many of its seconds the process observed, as a <see cref="MetricPoint"/> records it.
/// </summary>
public sealed class SecurityPoint
{
    /// <summary>How many counters a point holds: one per <see cref="SecurityCounter"/>.</summary>
    public const int Count = (int)SecurityCounter.RulesTimeouts + 1;

    private readonly long[] _counts;

    public SecurityPoint(long start, int width, int coveredSeconds, ReadOnlySpan<long> counts)
    {
        Start = start;
        Width = width;
        CoveredSeconds = coveredSeconds;
        _counts = new long[Count];
        counts[..Math.Min(counts.Length, Count)].CopyTo(_counts);
    }

    /// <summary>The first second, as Unix time.</summary>
    public long Start { get; }

    /// <summary>The length in seconds: 60 for a minute, the span asked for in a summary.</summary>
    public int Width { get; }

    /// <summary>How many of <see cref="Width"/>'s seconds the process observed; 0 for a stretch nobody observed.</summary>
    public int CoveredSeconds { get; }

    public ReadOnlySpan<long> Counts => _counts;

    public long this[SecurityCounter counter] => _counts[(int)counter];

    /// <summary>Whether any counter moved.</summary>
    public bool HasCounts => _counts.AsSpan().ContainsAnyExcept(0L);

    /// <summary>One point for all of <paramref name="points"/>: the counts and the observed seconds add up.</summary>
    public static SecurityPoint Combine(IEnumerable<SecurityPoint> points, long start, int width)
    {
        ArgumentNullException.ThrowIfNull(points);
        Span<long> sum = stackalloc long[Count];
        long covered = 0;
        foreach (var point in points)
        {
            covered += point.CoveredSeconds;
            for (var i = 0; i < Count; i++)
            {
                sum[i] += point._counts[i];
            }
        }

        return new SecurityPoint(start, width, (int)Math.Min(covered, int.MaxValue), sum);
    }

    /// <summary>A stretch with nothing counted and nothing observed.</summary>
    public static SecurityPoint Empty(long start, int width) => new(start, width, 0, []);
}

/// <summary>One finished minute of one security series, keyed by the object's typed ID and the minute: what the rollup job writes.</summary>
/// <param name="Subject">What the minute is about.</param>
/// <param name="SubjectId">The typed-ID string (<c>aup_...</c>, <c>rls_...</c>); empty for <see cref="SecurityRollupSubject.Total"/>.</param>
/// <param name="Point">The minute's figures.</param>
public readonly record struct SecurityRollup(SecurityRollupSubject Subject, string SubjectId, SecurityPoint Point);
