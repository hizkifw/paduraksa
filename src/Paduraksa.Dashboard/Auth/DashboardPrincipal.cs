using System.Security.Claims;

using Paduraksa.Application.Identity;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Auth;

/// <summary>
/// The signed-in user as the dashboard's components see them: the <see cref="SessionIdentity"/> the request authenticated with, and the
/// permissions the user holds right now (re-read on every session re-check). Components hide or disable what the user may not do with
/// <see cref="Has"/>; the Application services still decide, and refuse, on their own.
/// </summary>
public sealed class DashboardPrincipal : ClaimsPrincipal
{
    public DashboardPrincipal(SessionIdentity session, IReadOnlySet<Permission> permissions)
        : base(session)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        Session = session;
        Permissions = permissions;
    }

    /// <summary>The session, its user and the client it was opened from.</summary>
    public SessionIdentity Session { get; }

    /// <summary>What the user may do.</summary>
    public IReadOnlySet<Permission> Permissions { get; }

    /// <summary>The name to show: the display name, or the username.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Session.Session.DisplayName) ? Session.Session.Username : Session.Session.DisplayName;

    /// <summary>Whether the user holds <paramref name="permission"/>.</summary>
    public bool Has(Permission permission) => Permissions.Contains(permission);
}

/// <summary>Reading the <see cref="DashboardPrincipal"/> off an authentication state's user.</summary>
public static class DashboardPrincipalExtensions
{
    /// <summary>The dashboard user, or null when <paramref name="user"/> is anonymous.</summary>
    public static DashboardPrincipal? AsDashboardUser(this ClaimsPrincipal? user) => user as DashboardPrincipal;

    /// <summary>Whether <paramref name="user"/> is a signed-in dashboard user holding <paramref name="permission"/>.</summary>
    public static bool Has(this ClaimsPrincipal? user, Permission permission) => user is DashboardPrincipal principal && principal.Has(permission);
}
