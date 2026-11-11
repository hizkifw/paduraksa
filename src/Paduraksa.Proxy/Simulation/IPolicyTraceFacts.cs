using Paduraksa.Proxy.Gate;

namespace Paduraksa.Proxy.Simulation;

/// <summary>A policy that adds structured facts to its step of the simulator's trace, beside the sentence
/// <see cref="IRequestPolicy.Explain"/> gives. Called only by the recording tracer, right after <c>Evaluate</c>, so a policy
/// reads what its own simulated evaluation left on the request's state.</summary>
internal interface IPolicyTraceFacts
{
    void AddFacts(in RequestView request, PolicyDecision verdict, List<GateFact> facts);
}
