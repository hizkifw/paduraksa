using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Identity;

/// <summary>The identity services behind <see cref="IDashboardIdentity"/>, one per circuit; successful changes are announced on the <see cref="IdentityChangeFeed"/>.</summary>
internal sealed class DashboardIdentity(
    UserService users,
    SessionService sessions,
    ApiTokenService tokens,
    TwoFactorService twoFactor,
    IPasswordCheckSlots passwordChecks,
    IdentityChangeFeed feed) : IDashboardIdentity
{
    public Task<UserResult> GetUserAsync(CommandOrigin origin, UserId id, CancellationToken cancellationToken = default) =>
        users.GetAsync(origin, id, cancellationToken);

    public Task<UserListResult> ListUsersAsync(CommandOrigin origin, CancellationToken cancellationToken = default) =>
        users.ListAsync(origin, cancellationToken);

    public async Task<UserResult> CreateUserAsync(CommandOrigin origin, CreateUserRequest request, CancellationToken cancellationToken = default) =>
        Announce(await users.CreateAsync(origin, request, cancellationToken).ConfigureAwait(false), r => r.Succeeded);

    public async Task<UserResult> UpdateUserAsync(CommandOrigin origin, UserId id, UpdateUserRequest request, CancellationToken cancellationToken = default) =>
        Announce(await users.UpdateAsync(origin, id, request, cancellationToken).ConfigureAwait(false), r => r.Succeeded);

    public async Task<IdentityResult> DeleteUserAsync(CommandOrigin origin, UserId id, CancellationToken cancellationToken = default) =>
        Announce(await users.DeleteAsync(origin, id, cancellationToken).ConfigureAwait(false), r => r.Succeeded);

    public Task<IdentityResult?> ChangePasswordAsync(CommandOrigin origin, ChangePasswordRequest request, SessionId current, CancellationToken cancellationToken = default) =>
        WithPasswordCheckAsync(
            async () => Announce(await users.ChangePasswordAsync(origin, request, current, cancellationToken).ConfigureAwait(false), r => r.Succeeded),
            cancellationToken);

    public Task<TotpEnrollmentResult?> BeginTotpAsync(CommandOrigin origin, string password, CancellationToken cancellationToken = default) =>
        WithPasswordCheckAsync(() => twoFactor.BeginEnrollmentAsync(origin, password, cancellationToken), cancellationToken);

    public async Task<RecoveryCodesResult> ConfirmTotpAsync(CommandOrigin origin, string code, CancellationToken cancellationToken = default) =>
        Announce(await twoFactor.ConfirmEnrollmentAsync(origin, code, cancellationToken).ConfigureAwait(false), r => r.Status == IdentityStatus.Succeeded);

    public Task<RecoveryCodesResult?> RegenerateRecoveryCodesAsync(CommandOrigin origin, string password, CancellationToken cancellationToken = default) =>
        WithPasswordCheckAsync(() => twoFactor.RegenerateRecoveryCodesAsync(origin, password, cancellationToken), cancellationToken);

    public Task<IdentityResult?> DisableTotpAsync(CommandOrigin origin, string password, CancellationToken cancellationToken = default) =>
        WithPasswordCheckAsync(
            async () => Announce(await twoFactor.DisableAsync(origin, password, cancellationToken).ConfigureAwait(false), r => r.Succeeded),
            cancellationToken);

    public Task<SessionListResult> ListSessionsAsync(CommandOrigin origin, UserId owner, SessionId? current, CancellationToken cancellationToken = default) =>
        sessions.ListAsync(origin, owner, current, cancellationToken);

    public async Task<IdentityResult> RevokeSessionAsync(CommandOrigin origin, SessionId id, CancellationToken cancellationToken = default) =>
        Announce(await sessions.RevokeAsync(origin, id, cancellationToken).ConfigureAwait(false), r => r.Succeeded);

    public async Task<IdentityResult> RevokeOtherSessionsAsync(CommandOrigin origin, SessionId keep, CancellationToken cancellationToken = default) =>
        Announce(await sessions.RevokeOthersAsync(origin, keep, cancellationToken).ConfigureAwait(false), r => r.Succeeded);

    public Task<ApiTokenListResult> ListTokensAsync(CommandOrigin origin, UserId? owner = null, CancellationToken cancellationToken = default) =>
        tokens.ListAsync(origin, owner, cancellationToken);

    public Task<ApiTokenListResult> ListAllTokensAsync(CommandOrigin origin, CancellationToken cancellationToken = default) =>
        tokens.ListAllAsync(origin, cancellationToken);

    public async Task<ApiTokenResult> IssueTokenAsync(CommandOrigin origin, IssueApiTokenRequest request, CancellationToken cancellationToken = default) =>
        Announce(await tokens.IssueAsync(origin, request, cancellationToken).ConfigureAwait(false), r => r.Succeeded);

    public async Task<ApiTokenResult> RevokeTokenAsync(CommandOrigin origin, ApiTokenId id, CancellationToken cancellationToken = default) =>
        Announce(await tokens.RevokeAsync(origin, id, cancellationToken).ConfigureAwait(false), r => r.Succeeded);

    public IDisposable Watch(Func<Task> onChanged) => feed.Watch(onChanged);

    private T Announce<T>(T result, Func<T, bool> changed)
    {
        if (changed(result))
        {
            feed.Publish();
        }

        return result;
    }

    private async Task<T?> WithPasswordCheckAsync<T>(Func<Task<T>> run, CancellationToken cancellationToken)
        where T : class
    {
        using var slot = await passwordChecks.EnterAsync(cancellationToken).ConfigureAwait(false);
        return slot is null ? null : await run().ConfigureAwait(false);
    }
}
