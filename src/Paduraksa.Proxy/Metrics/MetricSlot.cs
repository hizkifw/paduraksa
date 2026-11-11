namespace Paduraksa.Proxy.Metrics;

/// <summary>The kinds of object that own a metric slot (PLAN §6.3, §7.4): one slot registry and one set of counter tables each.</summary>
public enum MetricSubject
{
    Host = 0,
    Route = 1,
    Upstream = 2,
    Destination = 3,
}

/// <summary>
/// A stable index into one subject's counter tables, assigned by the append-only <see cref="MetricSlotRegistry"/> and never
/// derived from a snapshot's array position, so an object's counters continue across every publish.
/// </summary>
/// <param name="Index">The slot, or -1 for "none" (a compile that has no registry, or an object that owns no counters).</param>
/// <param name="Version">The slot's version when it was handed out; a request that finishes after the slot was rented to another object is dropped.</param>
public readonly record struct MetricSlot(int Index, int Version = 0)
{
    /// <summary>No slot: recording is skipped.</summary>
    public static MetricSlot None => new(-1);

    public bool IsNone => Index < 0;
}
