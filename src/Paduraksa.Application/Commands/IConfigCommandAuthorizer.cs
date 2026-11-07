using Paduraksa.Contracts.Commands;

namespace Paduraksa.Application.Commands;

/// <summary>Decides whether an origin may run a command (PLAN §6.1 step 1). Denials are audited.</summary>
public interface IConfigCommandAuthorizer
{
    ValueTask<bool> IsAllowedAsync(ConfigCommand command, CommandOrigin origin, CancellationToken cancellationToken);
}
