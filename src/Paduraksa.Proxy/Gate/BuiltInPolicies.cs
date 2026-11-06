using Paduraksa.Proxy.Net;

namespace Paduraksa.Proxy.Gate;

/// <summary>Always returns the same decision: a static deny (e.g. 403 or "maintenance"), or a drop.</summary>
public sealed class StaticPolicy(PolicyDecision decision) : IRequestPolicy
{
    public PolicyDecision Decision { get; } = decision;

    public PolicyDecision Evaluate(in RequestView request) => Decision;
}

/// <summary>
/// Lets a request through only when the resolved client address (<see cref="GateRequestState.ClientAddress"/>: behind a
/// trusted proxy, the real client) is in <paramref name="allowed"/>; otherwise applies <paramref name="onDenied"/>.
/// One binary search over the compiled ranges (PLAN §7.3), 0 B.
/// </summary>
public sealed class IpAllowListPolicy(CidrSet allowed, PolicyDecision onDenied) : IRequestPolicy
{
    public CidrSet Allowed { get; } = allowed ?? throw new ArgumentNullException(nameof(allowed));

    public PolicyDecision OnDenied { get; } = onDenied.Action == PolicyAction.Continue
        ? throw new ArgumentException("A denial must respond or drop.", nameof(onDenied))
        : onDenied;

    public PolicyDecision Evaluate(in RequestView request) =>
        Allowed.Contains(request.State.ClientAddress) ? PolicyDecision.Continue : OnDenied;
}
