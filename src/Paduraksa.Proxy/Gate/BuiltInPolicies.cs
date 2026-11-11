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
