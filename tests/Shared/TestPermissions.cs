// Linked into the test projects that build Application services (<Compile Include Link>), so it compiles into each test
// assembly as an internal type of its own.
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Core.Model;

namespace Paduraksa.Tests.Shared;

/// <summary>
/// The <see cref="IPermissionEvaluator"/> of tests that don't exercise identity: every origin holds the same permissions,
/// whoever it is. Production code has no allow-everything default, so a test says what it is allowed to do.
/// </summary>
internal sealed class TestPermissions : IPermissionEvaluator
{
    private readonly HashSet<Permission> _held;

    private TestPermissions(IEnumerable<Permission> held) => _held = [.. held];

    /// <summary>Holds every permission, as the system and the CLI do.</summary>
    public static TestPermissions AllowAll { get; } = new(RolePermissions.All);

    /// <summary>Holds nothing.</summary>
    public static TestPermissions Deny { get; } = new([]);

    public static TestPermissions Holding(params Permission[] held) => new(held);

    public ValueTask<IReadOnlySet<Permission>> GetPermissionsAsync(Actor actor, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlySet<Permission>>(_held);

    public ValueTask<bool> IsAllowedAsync(CommandOrigin origin, Permission permission, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_held.Contains(permission));
}
