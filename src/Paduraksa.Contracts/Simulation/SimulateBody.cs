namespace Paduraksa.Contracts.Simulation;

/// <summary>The body of <c>POST /api/v1/simulate</c>: which config to run the request against, and the request.</summary>
/// <param name="Request">The request to ask "what would this get, and why" about.</param>
/// <param name="Target">The config to simulate against; null for the live one.</param>
public sealed record SimulateBody(SimulationRequest Request, SimulationTarget? Target = null);
