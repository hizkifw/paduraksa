namespace Paduraksa.Proxy.Metrics;

/// <summary>
/// What the gate did with a request, counted per host and per route next to the status class (docs/design/observability.md "Metrics (M9)"). The refusals are
/// the policy kinds the security page already tells apart; the rest say why a request got an answer from the gate itself.
/// </summary>
public enum GateOutcome : byte
{
    /// <summary>Not set by a policy or the forward: the final answer is classified from what the request is (<see cref="GateOutcomes.Classify"/>).</summary>
    Unset = 0,

    /// <summary>Passed every check and handed to YARP.</summary>
    Forwarded,

    /// <summary>Answered by the host itself: the HTTPS redirect, robots.txt, a Redirect or Respond host, an ACME challenge, the proof-of-work verify endpoint.</summary>
    Answered,

    /// <summary>An access list refused it by address, country or password.</summary>
    RefusedAccessList,

    /// <summary>A rule set blocked it.</summary>
    RefusedRuleSet,

    /// <summary>A rate limit turned it away.</summary>
    RefusedRateLimit,

    /// <summary>The proof-of-work page or a refusal of its token.</summary>
    RefusedProofOfWork,

    /// <summary>The forward-auth provider's refusal, redirect or failure.</summary>
    RefusedForwardAuth,

    /// <summary>Request target, header or body limits: 413, 414, 431.</summary>
    RefusedRequestLimits,

    /// <summary>The connection could not serve the host: a 421 (TLS policy or no certificate for it), an origin lockdown or a missing client certificate.</summary>
    RefusedConnection,

    /// <summary>The host has no route for the path.</summary>
    NoRoute,

    /// <summary>The snapshot's 503: no config is live yet, or the route has no endpoint.</summary>
    Unavailable,

    /// <summary>The connection was dropped without an answer.</summary>
    Dropped,

    /// <summary>No host matched: the default behaviour answered (404, 421 or a closed connection).</summary>
    UnknownHost,

    /// <summary>A policy refused it that has no kind of its own.</summary>
    RefusedOther,
}
