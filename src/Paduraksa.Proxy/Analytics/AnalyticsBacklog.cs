namespace Paduraksa.Proxy.Analytics;

/// <summary>
/// The closed buckets waiting to be written to the database, between the drain that closes them and the job that persists them (docs/design/observability.md "Traffic analytics",
/// analytics). Bounded, so a database that stays down loses the oldest buckets and never memory. A bucket the job has taken stays
/// visible to readers until the write is confirmed, so a chart never has a hole between "closed" and "stored".
/// </summary>
public sealed class AnalyticsBacklog
{
    /// <summary>The most rollups held: a day of five-minute buckets for a dozen busy hosts.</summary>
    public const int Capacity = 4_096;

    private readonly Lock _gate = new();
    private readonly List<AnalyticsRollup> _pending = [];
    private List<AnalyticsRollup> _inFlight = [];

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count + _inFlight.Count;
            }
        }
    }

    public void Add(IEnumerable<AnalyticsRollup> rollups)
    {
        ArgumentNullException.ThrowIfNull(rollups);
        lock (_gate)
        {
            _pending.AddRange(rollups);
            Trim();
        }
    }

    /// <summary>Hands everything waiting to the writer. It is still returned by <see cref="Unwritten"/> until <see cref="Acknowledge"/> or <see cref="Requeue"/>.</summary>
    public IReadOnlyList<AnalyticsRollup> Take()
    {
        lock (_gate)
        {
            _inFlight.AddRange(_pending);
            _pending.Clear();
            return [.. _inFlight];
        }
    }

    /// <summary>The write succeeded: <paramref name="written"/> are in the database.</summary>
    public void Acknowledge(IReadOnlyList<AnalyticsRollup> written)
    {
        ArgumentNullException.ThrowIfNull(written);
        lock (_gate)
        {
            HashSet<AnalyticsRollup> done = new(written, ReferenceEqualityComparer.Instance);
            List<AnalyticsRollup> remaining = [];
            foreach (var rollup in _inFlight)
            {
                if (!done.Contains(rollup))
                {
                    remaining.Add(rollup);
                }
            }

            _inFlight = remaining;
        }
    }

    /// <summary>The write failed: the taken rollups go back to waiting, ahead of newer ones.</summary>
    public void Requeue()
    {
        lock (_gate)
        {
            _pending.InsertRange(0, _inFlight);
            _inFlight = [];
            Trim();
        }
    }

    /// <summary>Everything not yet confirmed written, oldest first, for a reader to add to what the database holds.</summary>
    public IReadOnlyList<AnalyticsRollup> Unwritten()
    {
        lock (_gate)
        {
            return [.. _inFlight, .. _pending];
        }
    }

    private void Trim()
    {
        var excess = _pending.Count + _inFlight.Count - Capacity;
        if (excess > 0)
        {
            _pending.RemoveRange(0, Math.Min(excess, _pending.Count));
        }
    }
}
