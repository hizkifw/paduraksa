using System.Collections.Concurrent;

namespace Paduraksa.Proxy.Metrics;

/// <summary>
/// Where the sampler leaves each finished minute and the rollup job picks them up. Bounded: a job that falls behind (a locked
/// database, say) loses the oldest minutes and counts them, rather than letting memory grow.
/// </summary>
public sealed class MetricRollupBacklog
{
    /// <summary>The most minutes kept waiting: an hour of 150 series.</summary>
    public const int Capacity = 9_000;

    private readonly ConcurrentQueue<MetricRollup> _queue = new();
    private long _count;
    private long _dropped;

    /// <summary>Minutes lost because the job didn't keep up.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public int Count => (int)Interlocked.Read(ref _count);

    public void Enqueue(in MetricRollup rollup)
    {
        _queue.Enqueue(rollup);
        if (Interlocked.Increment(ref _count) > Capacity && _queue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _count);
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Takes up to <paramref name="max"/> minutes, oldest first.</summary>
    public List<MetricRollup> Drain(int max = Capacity)
    {
        List<MetricRollup> taken = [];
        while (taken.Count < max && _queue.TryDequeue(out var rollup))
        {
            Interlocked.Decrement(ref _count);
            taken.Add(rollup);
        }

        return taken;
    }

    /// <summary>Puts minutes back at the end of the queue, after a write that failed (they sort by minute on the way in).</summary>
    public void Requeue(IReadOnlyList<MetricRollup> rollups)
    {
        foreach (var rollup in rollups)
        {
            Enqueue(rollup);
        }
    }
}
