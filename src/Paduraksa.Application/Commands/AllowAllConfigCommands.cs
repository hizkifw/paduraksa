using Paduraksa.Contracts.Commands;

namespace Paduraksa.Application.Commands;

/// <summary>
/// The authorizer for a config service built without the identity services (tests): allows everything. The server registers
/// <c>AddPaduraksaIdentity</c>, which replaces it with the permission-based one (PLAN §10.1).
/// </summary>
internal sealed class AllowAllConfigCommands : IConfigCommandAuthorizer
{
    public static AllowAllConfigCommands Instance { get; } = new();

    public ValueTask<bool> IsAllowedAsync(ConfigCommand command, CommandOrigin origin, CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);
}
