namespace Paduraksa.Proxy.Gate;

/// <summary>Always returns the same decision: a static deny (e.g. 403 or "maintenance"), or a drop.</summary>
public sealed class StaticPolicy(PolicyDecision decision) : IRequestPolicy
{
    public PolicyDecision Decision { get; } = decision;

    public PolicyDecision Evaluate(in RequestView request) => Decision;
}

/// <summary>
/// Lets a request through only when the client address passes <paramref name="allows"/>; otherwise applies
/// <paramref name="onDenied"/>. The predicate is the M1 stand-in for the compiled CIDR set (PLAN §7.3); it receives
/// the resolved client address (IPv4 as IPv4-mapped IPv6) and must be allocation-free.
/// </summary>
public sealed class IpAllowListPolicy(Func<UInt128, bool> allows, PolicyDecision onDenied) : IRequestPolicy
{
    private readonly Func<UInt128, bool> _allows = allows ?? throw new ArgumentNullException(nameof(allows));

    public PolicyDecision OnDenied { get; } = onDenied.Action == PolicyAction.Continue
        ? throw new ArgumentException("A denial must respond or drop.", nameof(onDenied))
        : onDenied;

    public PolicyDecision Evaluate(in RequestView request) =>
        _allows(request.State.ClientAddress) ? PolicyDecision.Continue : OnDenied;
}
