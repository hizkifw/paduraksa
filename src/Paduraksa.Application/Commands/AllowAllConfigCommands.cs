using Paduraksa.Contracts.Commands;

namespace Paduraksa.Application.Commands;

/// <summary>
/// The authorizer until roles and scopes exist: allows everything. The surfaces already authenticate their callers,
/// and in M2 only admins reach them.
/// </summary>
internal sealed class AllowAllConfigCommands : IConfigCommandAuthorizer
{
    public static AllowAllConfigCommands Instance { get; } = new();

    // TODO(M4): check the actor's role (Owner/Admin/Operator may write config) and a token's scopes (hosts:write, …).
    public ValueTask<bool> IsAllowedAsync(ConfigCommand command, CommandOrigin origin, CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);
}
