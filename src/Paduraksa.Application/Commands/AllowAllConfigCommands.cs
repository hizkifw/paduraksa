using Paduraksa.Contracts.Commands;

namespace Paduraksa.Application.Commands;

/// <summary>The authorizer for a config service built without the identity services (tests): allows everything.</summary>
internal sealed class AllowAllConfigCommands : IConfigCommandAuthorizer
{
    public static AllowAllConfigCommands Instance { get; } = new();

    public ValueTask<bool> IsAllowedAsync(ConfigCommand command, CommandOrigin origin, CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);
}
