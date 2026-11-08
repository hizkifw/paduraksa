using System.Collections.Frozen;

using Paduraksa.Core.Model;

namespace Paduraksa.Application.Identity;

/// <summary>
/// Which <see cref="Permission"/>s each <see cref="Role"/> grants (PLAN §10.1). Roles are data here, not code paths, so custom
/// roles can be added later by supplying another set. A user's permissions are the union of their roles'; an API token's are
/// that union narrowed to its scopes (<see cref="Narrow"/>).
/// </summary>
public static class RolePermissions
{
    private static readonly FrozenSet<Permission> AllPermissions = Enum.GetValues<Permission>().ToFrozenSet();

    private static readonly FrozenDictionary<Role, FrozenSet<Permission>> ByRole = new Dictionary<Role, FrozenSet<Permission>>
    {
        [Role.Owner] = AllPermissions,
        [Role.Admin] = Set(
            Permission.ConfigRead, Permission.ConfigWrite, Permission.CertificatesRead, Permission.CertificatesWrite,
            Permission.ProvidersRead, Permission.ProvidersWrite, Permission.UsersRead, Permission.AuditRead,
            Permission.Simulate, Permission.TokensOwn),
        [Role.Operator] = Set(
            Permission.ConfigRead, Permission.ConfigWrite, Permission.CertificatesRead, Permission.CertificatesWrite,
            Permission.ProvidersRead, Permission.Simulate, Permission.TokensOwn),
        [Role.Viewer] = Set(
            Permission.ConfigRead, Permission.CertificatesRead, Permission.ProvidersRead, Permission.Simulate, Permission.TokensOwn),
        [Role.Auditor] = Set(
            Permission.ConfigRead, Permission.CertificatesRead, Permission.ProvidersRead, Permission.AuditRead, Permission.TokensOwn),
    }.ToFrozenDictionary();

    /// <summary>Every permission there is.</summary>
    public static IReadOnlySet<Permission> All => AllPermissions;

    /// <summary>What <paramref name="role"/> grants.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The role is undefined.</exception>
    public static IReadOnlySet<Permission> Of(Role role) =>
        ByRole.TryGetValue(role, out var set) ? set : throw new ArgumentOutOfRangeException(nameof(role), role, "Undefined role.");

    /// <summary>The union of what <paramref name="roles"/> grant.</summary>
    public static IReadOnlySet<Permission> Of(IEnumerable<Role> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return roles.SelectMany(Of).ToFrozenSet();
    }

    /// <summary>
    /// What a token with <paramref name="scopes"/> may do for an owner who holds <paramref name="owner"/>: the intersection, so a
    /// token never has more than its user (a scope the owner lacks, or has lost since, grants nothing).
    /// </summary>
    public static IReadOnlySet<Permission> Narrow(IReadOnlySet<Permission> owner, IEnumerable<Permission> scopes)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(scopes);
        return scopes.Where(owner.Contains).ToFrozenSet();
    }

    private static FrozenSet<Permission> Set(params Permission[] permissions) => permissions.ToFrozenSet();
}
