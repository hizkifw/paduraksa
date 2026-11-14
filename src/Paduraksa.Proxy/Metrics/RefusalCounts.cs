namespace Paduraksa.Proxy.Metrics;

/// <summary>
/// What the gate turned away in a window, by <see cref="GateOutcome"/>: the policy kinds that make up <see cref="MetricPoint.Refused"/>,
/// and the four answers the gate gives without a policy (no route, unavailable, dropped, unknown host), which are not counted as refused.
/// </summary>
public readonly record struct RefusalCounts
{
    public long AccessList { get; init; }

    public long RuleSet { get; init; }

    public long RateLimit { get; init; }

    public long ProofOfWork { get; init; }

    public long ForwardAuth { get; init; }

    public long RequestLimits { get; init; }

    public long Connection { get; init; }

    public long Other { get; init; }

    public long NoRoute { get; init; }

    public long Unavailable { get; init; }

    public long Dropped { get; init; }

    public long UnknownHost { get; init; }

    /// <summary>The policy kinds' sum: what <see cref="MetricPoint.Refused"/> is when the window was counted with its breakdown.</summary>
    public long Policy => AccessList + RuleSet + RateLimit + ProofOfWork + ForwardAuth + RequestLimits + Connection + Other;

    public static RefusalCounts operator +(RefusalCounts left, RefusalCounts right) => new()
    {
        AccessList = left.AccessList + right.AccessList,
        RuleSet = left.RuleSet + right.RuleSet,
        RateLimit = left.RateLimit + right.RateLimit,
        ProofOfWork = left.ProofOfWork + right.ProofOfWork,
        ForwardAuth = left.ForwardAuth + right.ForwardAuth,
        RequestLimits = left.RequestLimits + right.RequestLimits,
        Connection = left.Connection + right.Connection,
        Other = left.Other + right.Other,
        NoRoute = left.NoRoute + right.NoRoute,
        Unavailable = left.Unavailable + right.Unavailable,
        Dropped = left.Dropped + right.Dropped,
        UnknownHost = left.UnknownHost + right.UnknownHost,
    };

    /// <summary>The counts in a host's or a route's slot layout (<see cref="RequestMetric.Outcome"/> onwards).</summary>
    internal static RefusalCounts FromSlot(ReadOnlySpan<long> counters) => new()
    {
        AccessList = counters[RequestMetric.Outcome + (int)GateOutcome.RefusedAccessList],
        RuleSet = counters[RequestMetric.Outcome + (int)GateOutcome.RefusedRuleSet],
        RateLimit = counters[RequestMetric.Outcome + (int)GateOutcome.RefusedRateLimit],
        ProofOfWork = counters[RequestMetric.Outcome + (int)GateOutcome.RefusedProofOfWork],
        ForwardAuth = counters[RequestMetric.Outcome + (int)GateOutcome.RefusedForwardAuth],
        RequestLimits = counters[RequestMetric.Outcome + (int)GateOutcome.RefusedRequestLimits],
        Connection = counters[RequestMetric.Outcome + (int)GateOutcome.RefusedConnection],
        Other = counters[RequestMetric.Outcome + (int)GateOutcome.RefusedOther],
        NoRoute = counters[RequestMetric.Outcome + (int)GateOutcome.NoRoute],
        Unavailable = counters[RequestMetric.Outcome + (int)GateOutcome.Unavailable],
        Dropped = counters[RequestMetric.Outcome + (int)GateOutcome.Dropped],
        UnknownHost = counters[RequestMetric.Outcome + (int)GateOutcome.UnknownHost],
    };
}
