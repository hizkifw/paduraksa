using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Simulation;

namespace Paduraksa.Application.Simulation;

/// <summary>Decides whether an origin may run a simulation against a target. Every simulation needs the
/// <c>simulate</c> permission; a draft command or document, and a stored revision, show what the config contains, so
/// they need the permission to read the config as well.</summary>
public interface ISimulationAuthorizer
{
    ValueTask<bool> IsAllowedAsync(SimulationTarget target, CommandOrigin origin, CancellationToken cancellationToken);
}

/// <summary>Allows everything: the default of a <see cref="RequestSimulator"/> built without identity, for tests.</summary>
internal sealed class AllowAllSimulations : ISimulationAuthorizer
{
    public static AllowAllSimulations Instance { get; } = new();

    public ValueTask<bool> IsAllowedAsync(SimulationTarget target, CommandOrigin origin, CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);
}
