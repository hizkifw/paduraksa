using Paduraksa.Application.Identity;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Auth;

/// <summary>
/// What the dashboard asks of the identity services about a signed-in circuit: is its session still live (and as whom), and what may the
/// user do. A seam so the auth state can be tested without a database; <see cref="DashboardSessions"/> is the real one.
/// </summary>
public interface IDashboardSessions
{
    /// <summary>The session's user (with fresh roles) while it is live and complete; null once it is revoked, expired or its user disabled or gone.</summary>
    Task<SessionPrincipal?> CheckAsync(SessionId session, bool countAsUse, CancellationToken cancellationToken);

    /// <summary>What <paramref name="user"/> may do right now.</summary>
    ValueTask<IReadOnlySet<Permission>> GetPermissionsAsync(UserId user, CancellationToken cancellationToken);
}

/// <summary>The identity services behind <see cref="IDashboardSessions"/>: <see cref="SessionService.CheckAsync"/> and <see cref="IPermissionEvaluator"/>.</summary>
internal sealed class DashboardSessions(SessionService sessions, IPermissionEvaluator permissions) : IDashboardSessions
{
    public async Task<SessionPrincipal?> CheckAsync(SessionId session, bool countAsUse, CancellationToken cancellationToken)
    {
        SessionValidation validation = await sessions.CheckAsync(session, countAsUse, cancellationToken).ConfigureAwait(false);
        return validation is { Status: SessionStatus.Valid, Principal: { } principal } ? principal : null;
    }

    public ValueTask<IReadOnlySet<Permission>> GetPermissionsAsync(UserId user, CancellationToken cancellationToken) =>
        permissions.GetPermissionsAsync(Actor.ForUser(user), cancellationToken);
}
