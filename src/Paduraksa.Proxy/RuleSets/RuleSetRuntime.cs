using System.Collections.Concurrent;

using Paduraksa.Core.Model;

namespace Paduraksa.Proxy.RuleSets;

/// <summary>How often one rule set refused, dropped or let a request through by a rule, since the process started. Lock-free
/// counters the security overview reads; they survive a publish, because a set keeps its ID.</summary>
public sealed class RuleSetCounters
{
    private long _denied;
    private long _dropped;
    private long _allowed;
    private long _timeouts;

    public long Denied => Interlocked.Read(ref _denied);

    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Requests an <see cref="RuleAction.Allow"/> rule took out of the set's later rules.</summary>
    public long Allowed => Interlocked.Read(ref _allowed);

    /// <summary>Regex matches that ran past their timeout and were decided by the rule's action instead.</summary>
    public long Timeouts => Interlocked.Read(ref _timeouts);

    internal void CountTimeout() => Interlocked.Increment(ref _timeouts);

    internal void Count(RuleAction action)
    {
        switch (action)
        {
            case RuleAction.Deny:
                Interlocked.Increment(ref _denied);
                break;
            case RuleAction.Drop:
                Interlocked.Increment(ref _dropped);
                break;
            default:
                Interlocked.Increment(ref _allowed);
                break;
        }
    }
}

/// <summary>The process's rule-set counters, one object per set ID, made when a snapshot first compiles the set.</summary>
public sealed class RuleSetRuntime
{
    private readonly ConcurrentDictionary<RuleSetId, RuleSetCounters> _counters = new();
    private readonly Lock _gate = new();
    private KeyValuePair<RuleSetId, RuleSetCounters>[] _all = [];

    /// <summary>The runtime a compile uses when its options name none.</summary>
    public static RuleSetRuntime Default { get; } = new();

    /// <summary>
    /// Every set's counters the process has made, in the order they were made. Only ever appended to, so an entry keeps its index:
    /// the metric sampler reads it every second and keys its series by that index without allocating.
    /// </summary>
    public ReadOnlySpan<KeyValuePair<RuleSetId, RuleSetCounters>> All => Volatile.Read(ref _all);

    /// <summary>The counters of <paramref name="id"/>: cold path, called once per set per compile.</summary>
    public RuleSetCounters CountersFor(RuleSetId id)
    {
        if (_counters.TryGetValue(id, out var counters))
        {
            return counters;
        }

        lock (_gate)
        {
            if (!_counters.TryGetValue(id, out counters))
            {
                counters = new RuleSetCounters();
                _counters[id] = counters;
                Volatile.Write(ref _all, [.. _all, new(id, counters)]);
            }

            return counters;
        }
    }
}
