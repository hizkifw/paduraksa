using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Identity;

/// <summary>
/// What the settings and account pages ask of the identity services (PLAN §5.2, §10.1): users, the caller's own password, second factor
/// and sessions, and API tokens. A seam so the pages can be tested without a database; <see cref="DashboardIdentity"/> is the real one.
/// Everything it hands out is a Contracts DTO or an Application result over one.
/// </summary>
/// <remarks>
/// Every call carries the caller's <see cref="CommandOrigin"/> and the services decide, as on the API: the pages only hide what the
/// caller may not do. The dashboard always acts through a session, so the API's rule that a token can't manage tokens or sessions has
/// nothing to refuse here. A call that checks a password waits for one of the server's password-check slots first (as the API does);
/// when none frees up in time it answers null and derives nothing.
/// </remarks>
public interface IDashboardIdentity
{
    /// <summary>One user: the caller themselves, or anyone with <c>users:read</c>.</summary>
    Task<UserResult> GetUserAsync(CommandOrigin origin, UserId id, CancellationToken cancellationToken = default);

    /// <summary>Every user, oldest first; needs <c>users:read</c>.</summary>
    Task<UserListResult> ListUsersAsync(CommandOrigin origin, CancellationToken cancellationToken = default);

    /// <summary>Creates a user; needs <c>users:write</c>.</summary>
    Task<UserResult> CreateUserAsync(CommandOrigin origin, CreateUserRequest request, CancellationToken cancellationToken = default);

    /// <summary>Changes a user's display name, roles or enabled state in one step, the last-Owner guard included; needs <c>users:write</c>.</summary>
    Task<UserResult> UpdateUserAsync(CommandOrigin origin, UserId id, UpdateUserRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes a user with their tokens and sessions; needs <c>users:write</c>.</summary>
    Task<IdentityResult> DeleteUserAsync(CommandOrigin origin, UserId id, CancellationToken cancellationToken = default);

    /// <summary>Changes the caller's own password and ends their other sessions; <paramref name="current"/> stays signed in. Null when busy.</summary>
    Task<IdentityResult?> ChangePasswordAsync(CommandOrigin origin, ChangePasswordRequest request, SessionId current, CancellationToken cancellationToken = default);

    /// <summary>Starts a TOTP enrolment (needs the password): the provisioning URI, once. Null when busy.</summary>
    Task<TotpEnrollmentResult?> BeginTotpAsync(CommandOrigin origin, string password, CancellationToken cancellationToken = default);

    /// <summary>Confirms the enrolment with a code from the authenticator: the recovery codes, once.</summary>
    Task<RecoveryCodesResult> ConfirmTotpAsync(CommandOrigin origin, string code, CancellationToken cancellationToken = default);

    /// <summary>Replaces the recovery codes (needs the password): the new ones, once. Null when busy.</summary>
    Task<RecoveryCodesResult?> RegenerateRecoveryCodesAsync(CommandOrigin origin, string password, CancellationToken cancellationToken = default);

    /// <summary>Turns the second factor off (needs the password). Null when busy.</summary>
    Task<IdentityResult?> DisableTotpAsync(CommandOrigin origin, string password, CancellationToken cancellationToken = default);

    /// <summary>The live sessions of <paramref name="owner"/>, newest first, <paramref name="current"/> marked; another user's need <c>sessions:all</c>.</summary>
    Task<SessionListResult> ListSessionsAsync(CommandOrigin origin, UserId owner, SessionId? current, CancellationToken cancellationToken = default);

    /// <summary>Ends one session: the caller's own, or anyone's with <c>sessions:all</c>.</summary>
    Task<IdentityResult> RevokeSessionAsync(CommandOrigin origin, SessionId id, CancellationToken cancellationToken = default);

    /// <summary>Ends every session of the caller except <paramref name="keep"/>.</summary>
    Task<IdentityResult> RevokeOtherSessionsAsync(CommandOrigin origin, SessionId keep, CancellationToken cancellationToken = default);

    /// <summary>The tokens of <paramref name="owner"/> (the caller when null), newest first, revoked ones included, never with a secret.</summary>
    Task<ApiTokenListResult> ListTokensAsync(CommandOrigin origin, UserId? owner = null, CancellationToken cancellationToken = default);

    /// <summary>Issues a token for the caller: its secret, once.</summary>
    Task<ApiTokenResult> IssueTokenAsync(CommandOrigin origin, IssueApiTokenRequest request, CancellationToken cancellationToken = default);

    /// <summary>Revokes a token: the caller's own, or anyone's with <c>tokens:all</c>.</summary>
    Task<ApiTokenResult> RevokeTokenAsync(CommandOrigin origin, ApiTokenId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls <paramref name="onChanged"/> after every identity change made through the dashboard (by any circuit), until the handle is
    /// disposed, so lists of users, tokens and sessions follow what other admins do. The callback runs off the renderer's thread.
    /// </summary>
    IDisposable Watch(Func<Task> onChanged);
}
