using Paduraksa.Proxy.Net;

namespace Paduraksa.Proxy.Gate;

/// <summary>Always returns the same decision: a static deny (e.g. 403 or "maintenance"), or a drop.</summary>
public sealed class StaticPolicy(PolicyDecision decision) : IRequestPolicy
{
    public PolicyDecision Decision { get; } = decision;

    public PolicyDecision Evaluate(in RequestView request) => Decision;

    string IRequestPolicy.Name => "static";

    string IRequestPolicy.Explain(in RequestView request, PolicyDecision verdict) => verdict.Action switch
    {
        PolicyAction.Drop => "always drops the request",
        PolicyAction.Respond => $"always answers {verdict.Response!.StatusCode}",
        _ => "lets every request through",
    };
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

    string IRequestPolicy.Name => "ipAllowList";

    string IRequestPolicy.Explain(in RequestView request, PolicyDecision verdict) =>
        verdict.Action == PolicyAction.Continue
            ? $"client {request.State.ClientAddress} is in the allow list ({Allowed.RangeCount} range(s))"
            : $"client {request.State.ClientAddress} is not in the allow list ({Allowed.RangeCount} range(s)): {(verdict.Action == PolicyAction.Drop ? "dropped" : "answered " + verdict.Response!.StatusCode)}";
}
