namespace Paduraksa.Proxy.Metrics;

/// <summary>The label values of the metrics: stable, snake_case, and the same in the Prometheus text, the OpenTelemetry tags and the dashboard.</summary>
public static class MetricNames
{
    private static readonly string[] StatusClasses = ["1xx", "2xx", "3xx", "4xx", "5xx", "none"];

    private static readonly string[] Outcomes =
    [
        "unset", "forwarded", "answered", "refused_access_list", "refused_rule_set", "refused_rate_limit", "refused_proof_of_work",
        "refused_forward_auth", "refused_request_limits", "refused_connection", "no_route", "unavailable", "dropped", "unknown_host",
        "refused_other",
    ];

    /// <summary>The status class label for a counter offset (0 = <c>1xx</c> to 4 = <c>5xx</c>, 5 = <c>none</c>).</summary>
    public static string StatusClass(int offset) => StatusClasses[offset];

    public static string Outcome(GateOutcome outcome) => Outcomes[(int)outcome];

    /// <summary>The outcomes a counter exists for (everything but <see cref="GateOutcome.Unset"/>, which is never counted).</summary>
    public static IReadOnlyList<GateOutcome> CountedOutcomes { get; } =
    [
        GateOutcome.Forwarded, GateOutcome.Answered, GateOutcome.RefusedAccessList, GateOutcome.RefusedRuleSet, GateOutcome.RefusedRateLimit,
        GateOutcome.RefusedProofOfWork, GateOutcome.RefusedForwardAuth, GateOutcome.RefusedRequestLimits, GateOutcome.RefusedConnection,
        GateOutcome.NoRoute, GateOutcome.Unavailable, GateOutcome.Dropped, GateOutcome.UnknownHost, GateOutcome.RefusedOther,
    ];

    /// <summary>Whether the outcome is a refusal by a policy or a limit, the figure the overview calls "refused".</summary>
    public static bool IsRefusal(GateOutcome outcome) => outcome is >= GateOutcome.RefusedAccessList and <= GateOutcome.RefusedConnection or GateOutcome.RefusedOther;
}
