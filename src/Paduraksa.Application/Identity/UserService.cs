using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Paduraksa.Application.Audit;
using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Data;
using Paduraksa.Data.Identity;

namespace Paduraksa.Application.Identity;

/// <summary>
/// User management for the admin surfaces: create, list, change roles, disable, delete, and the password
/// operations. Every mutation is checked against the origin's permissions, runs in one transaction with its audit event, and
/// refuses to leave the instance without an enabled Owner. Users come back as <see cref="UserSummary"/>, never as rows, and never with a
/// hash or a secret reference.
/// </summary>
public sealed partial class UserService
{
    /// <summary>The longest display name.</summary>
    public const int MaxDisplayNameLength = IdentityConfiguration.MaxDisplayNameLength;

    private static readonly Regex UsernamePattern = UsernameRegex();

    private readonly IDbContextFactory<ConfigDbContext> _contexts;
    private readonly TimeProvider _time;
    private readonly IdentityOptions _options;
    private readonly PasswordHasher _hasher;
    private readonly PasswordPolicy _policy;
    private readonly IdentityAudit _audit;
    private readonly IPermissionEvaluator _permissions;
    private readonly ITotpAuthenticator? _totp;
    private readonly ILogger _logger;

    internal UserService(
        IDbContextFactory<ConfigDbContext> contexts,
        TimeProvider time,
        IdentityOptions options,
        PasswordHasher hasher,
        PasswordPolicy policy,
        IdentityAudit audit,
        IPermissionEvaluator permissions,
        ITotpAuthenticator? totp,
        ILogger<UserService> logger)
    {
        _contexts = contexts;
        _time = time;
        _options = options;
        _hasher = hasher;
        _policy = policy;
        _audit = audit;
        _permissions = permissions;
        _totp = totp;
        _logger = logger;
    }

    /// <summary>Creates a user with a password and at least one role. Needs <see cref="Permission.UsersWrite"/>.</summary>
    public async Task<UserResult> CreateAsync(CommandOrigin origin, CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(request);
        if (!await _permissions.IsAllowedAsync(origin, Permission.UsersWrite, cancellationToken).ConfigureAwait(false))
        {
            return await DeniedUserAsync(origin, "user.create", null).ConfigureAwait(false);
        }

        var problem = CheckNewUser(request, out var username, out var displayName, out var roles);
        if (problem is not null)
        {
            return new UserResult(IdentityStatus.Invalid, Problem: problem);
        }

        var now = _time.GetUtcNow();
        var row = new UserRow
        {
            Id = UserId.New(),
            Username = username,
            DisplayName = displayName,
            PasswordHash = _hasher.Hash(request.Password),
            CreatedAt = now,
            UpdatedAt = now,
        };

        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                if (await context.Users.AnyAsync(u => u.Username == username, cancellationToken).ConfigureAwait(false))
                {
                    return new UserResult(IdentityStatus.Conflict, Problem: "That username is taken.");
                }

                context.Users.Add(row);
                context.RoleAssignments.AddRange(roles.Select(r => new RoleAssignmentRow { UserId = row.Id, Role = r }));
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await _audit.AppendAsync(
                    context,
                    IdentityAudit.Entry(origin, "user.create", AuditOutcome.Success, row.Id.ToString(), w => w.WriteString("username", username)),
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return new UserResult(IdentityStatus.Succeeded, IdentityMapping.ToSummary(row, roles, now));
    }

    /// <summary>Lists the users, oldest first. Needs <see cref="Permission.UsersRead"/>.</summary>
    public async Task<UserListResult> ListAsync(CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!await _permissions.IsAllowedAsync(origin, Permission.UsersRead, cancellationToken).ConfigureAwait(false))
        {
            return new UserListResult(IdentityStatus.Denied, []);
        }

        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var users = await context.Users.AsNoTracking().OrderBy(u => u.CreatedAt).ThenBy(u => u.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            var roles = (await context.RoleAssignments.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false)).ToLookup(r => r.UserId, r => r.Role);
            var now = _time.GetUtcNow();
            return new UserListResult(IdentityStatus.Succeeded, [.. users.Select(u => IdentityMapping.ToSummary(u, roles[u.Id], now))]);
        }
    }

    /// <summary>One user. A user may always read themselves; anyone else needs <see cref="Permission.UsersRead"/>.</summary>
    public async Task<UserResult> GetAsync(CommandOrigin origin, UserId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (origin.Actor.UserId != id && !await _permissions.IsAllowedAsync(origin, Permission.UsersRead, cancellationToken).ConfigureAwait(false))
        {
            return new UserResult(IdentityStatus.Denied);
        }

        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            return await LoadAsync(context, id, cancellationToken).ConfigureAwait(false) is { } user
                ? new UserResult(IdentityStatus.Succeeded, await SummaryAsync(context, user, cancellationToken).ConfigureAwait(false))
                : new UserResult(IdentityStatus.NotFound);
        }
    }

    /// <summary>
    /// Changes the display name, replaces the roles and/or disables or re-enables the user, all in one transaction: it succeeds or fails
    /// as a whole (the last-Owner guard included, so a refusal leaves the name unchanged too). Disabling ends the user's sessions
    /// and, because tokens are checked against their owner on every use, their tokens; enabling clears a lockout. Needs
    /// <see cref="Permission.UsersWrite"/>.
    /// </summary>
    public async Task<UserResult> UpdateAsync(CommandOrigin origin, UserId id, UpdateUserRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(request);
        if (!await _permissions.IsAllowedAsync(origin, Permission.UsersWrite, cancellationToken).ConfigureAwait(false))
        {
            return await DeniedUserAsync(origin, "user.update", id).ConfigureAwait(false);
        }

        string? displayName = null;
        if (request.DisplayName is not null)
        {
            displayName = IdentityMapping.Clean(request.DisplayName, MaxDisplayNameLength);
            if (displayName is null)
            {
                return new UserResult(IdentityStatus.Invalid, Problem: "The display name must not be empty.");
            }
        }

        List<Role>? roles = null;
        if (request.Roles is not null)
        {
            roles = [.. request.Roles.Distinct()];
            if (RolesProblem(roles) is { } problem)
            {
                return new UserResult(IdentityStatus.Invalid, Problem: problem);
            }
        }

        return await MutateAsync(origin, id, "user.update", async (context, user, now) =>
        {
            var removesOwner = roles is not null && !roles.Contains(Role.Owner);
            if ((removesOwner || request.Disabled == true) && await WouldLeaveNoOwnerAsync(context, id, cancellationToken).ConfigureAwait(false))
            {
                return IdentityStatus.LastOwner;
            }

            if (displayName is not null)
            {
                user.DisplayName = displayName;
            }

            if (request.Disabled is { } disabled)
            {
                if (disabled)
                {
                    await RevokeSessionsAsync(context, id, keep: null, now, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    IdentityMapping.ClearFailures(user);
                }

                user.Disabled = disabled;
            }

            if (roles is not null)
            {

                var current = await context.RoleAssignments.Where(r => r.UserId == id).ToListAsync(cancellationToken).ConfigureAwait(false);
                context.RoleAssignments.RemoveRange(current.Where(r => !roles.Contains(r.Role)));
                context.RoleAssignments.AddRange(roles.Where(r => current.TrueForAll(c => c.Role != r)).Select(r => new RoleAssignmentRow { UserId = id, Role = r }));
            }

            user.UpdatedAt = now;
            return IdentityStatus.Succeeded;
        },
        w =>
        {
            if (roles is not null)
            {
                w.WriteStartArray("roles");
                roles.Order().ToList().ForEach(r => w.WriteStringValue(r.ToName()));
                w.WriteEndArray();
            }

            if (request.Disabled is { } disabled)
            {
                w.WriteBoolean("disabled", disabled);
            }
        },
        cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Disables or re-enables a user. Disabling ends their sessions and (because tokens are checked against their owner on every
    /// use) their tokens; enabling clears a lockout. Needs <see cref="Permission.UsersWrite"/>.
    /// </summary>
    public Task<UserResult> SetDisabledAsync(CommandOrigin origin, UserId id, bool disabled, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return PermitThenAsync(origin, id, disabled ? "user.disable" : "user.enable", () =>
            MutateAsync(origin, id, disabled ? "user.disable" : "user.enable", async (context, user, now) =>
            {
                if (disabled)
                {
                    if (await WouldLeaveNoOwnerAsync(context, id, cancellationToken).ConfigureAwait(false))
                    {
                        return IdentityStatus.LastOwner;
                    }

                    await RevokeSessionsAsync(context, id, keep: null, now, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    IdentityMapping.ClearFailures(user);
                }

                user.Disabled = disabled;
                user.UpdatedAt = now;
                return IdentityStatus.Succeeded;
            },
            null,
            cancellationToken),
            cancellationToken);
    }

    /// <summary>Deletes a user with their tokens, sessions and recovery codes (and their TOTP secret in the vault). Needs <see cref="Permission.UsersWrite"/>.</summary>
    public async Task<IdentityResult> DeleteAsync(CommandOrigin origin, UserId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!await _permissions.IsAllowedAsync(origin, Permission.UsersWrite, cancellationToken).ConfigureAwait(false))
        {
            return await DeniedIdentityAsync(origin, "user.delete", id).ConfigureAwait(false);
        }

        SecretId? totp;
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var user = await LoadAsync(context, id, cancellationToken).ConfigureAwait(false);
                if (user is null)
                {
                    return new IdentityResult(IdentityStatus.NotFound);
                }

                if (await WouldLeaveNoOwnerAsync(context, id, cancellationToken).ConfigureAwait(false))
                {
                    return new IdentityResult(IdentityStatus.LastOwner);
                }

                totp = user.TotpSecretId;
                context.Users.Remove(user);
                await _audit.AppendAsync(
                    context,
                    IdentityAudit.Entry(origin, "user.delete", AuditOutcome.Success, id.ToString(), w => w.WriteString("username", user.Username)),
                    cancellationToken).ConfigureAwait(false);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await DeleteTotpSecretAsync(totp).ConfigureAwait(false);
        return new IdentityResult(IdentityStatus.Succeeded);
    }

    /// <summary>
    /// Changes the calling user's own password. Needs the current one (a stolen session alone is not enough, and a wrong guess counts
    /// toward the lockout), and ends every other session of the user; <paramref name="currentSession"/> stays signed in.
    /// </summary>
    public async Task<IdentityResult> ChangePasswordAsync(CommandOrigin origin, ChangePasswordRequest request, SessionId? currentSession, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(request);
        if (origin.Actor.Kind != ActorKind.User)
        {
            return await DeniedIdentityAsync(origin, "user.password.change", null).ConfigureAwait(false);
        }

        var id = origin.Actor.UserId!.Value;
        if (_policy.Check(request.NewPassword) is { } problem)
        {
            return new IdentityResult(IdentityStatus.Invalid, problem);
        }

        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var user = await LoadAsync(context, id, cancellationToken).ConfigureAwait(false);
            if (user is null || user.Disabled)
            {
                return new IdentityResult(IdentityStatus.NotFound);
            }

            var now = _time.GetUtcNow();
            var check = IdentityMapping.IsLocked(user, now)
                ? default
                : _hasher.Verify(request.CurrentPassword ?? string.Empty, user.PasswordHash);
            var newHash = check.Matches ? _hasher.Hash(request.NewPassword) : null;

            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                // Re-read inside the write lock: the counter and the hash change together or not at all.
                var fresh = await LoadAsync(context, id, cancellationToken).ConfigureAwait(false);
                if (fresh is null || fresh.Disabled)
                {
                    return new IdentityResult(IdentityStatus.NotFound); // deleted or disabled since the first read
                }

                user = fresh;
                if (newHash is null || IdentityMapping.IsLocked(user, now))
                {
                    var locked = IdentityMapping.RegisterFailure(user, now, _options);
                    if (locked)
                    {
                        // A proven session that keeps failing the password check is the suspicious one: it ends with all the others.
                        await RevokeSessionsAsync(context, id, keep: null, now, cancellationToken).ConfigureAwait(false);
                    }

                    user.UpdatedAt = now;
                    await _audit.AppendAsync(
                        context,
                        IdentityAudit.Entry(origin, "user.password.change", AuditOutcome.Denied, id.ToString(), w => w.WriteBoolean("lockedOut", locked)),
                        cancellationToken).ConfigureAwait(false);
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new IdentityResult(IdentityStatus.InvalidCredentials);
                }

                user.PasswordHash = newHash;
                user.UpdatedAt = now;
                IdentityMapping.ClearFailures(user);
                var revoked = await RevokeSessionsAsync(context, id, currentSession, now, cancellationToken).ConfigureAwait(false);
                await _audit.AppendAsync(
                    context,
                    IdentityAudit.Entry(origin, "user.password.change", AuditOutcome.Success, id.ToString(), w => w.WriteNumber("sessionsRevoked", revoked)),
                    cancellationToken).ConfigureAwait(false);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return new IdentityResult(IdentityStatus.Succeeded);
    }

    /// <summary>
    /// Sets a user's password without the old one (the CLI's <c>admin reset-password</c>): ends all their sessions and clears a
    /// lockout. Needs <see cref="Permission.UsersWrite"/>, which the CLI has.
    /// </summary>
    public async Task<IdentityResult> ResetPasswordAsync(CommandOrigin origin, string username, string newPassword, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(username);
        if (!await _permissions.IsAllowedAsync(origin, Permission.UsersWrite, cancellationToken).ConfigureAwait(false))
        {
            return await DeniedIdentityAsync(origin, "user.password.reset", null).ConfigureAwait(false);
        }

        if (_policy.Check(newPassword) is { } problem)
        {
            return new IdentityResult(IdentityStatus.Invalid, problem);
        }

        var hash = _hasher.Hash(newPassword);
        var name = username.Trim();
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var user = await context.Users.SingleOrDefaultAsync(u => u.Username == name, cancellationToken).ConfigureAwait(false);
                if (user is null)
                {
                    return new IdentityResult(IdentityStatus.NotFound);
                }

                var now = _time.GetUtcNow();
                user.PasswordHash = hash;
                user.UpdatedAt = now;
                IdentityMapping.ClearFailures(user);
                var revoked = await RevokeSessionsAsync(context, user.Id, keep: null, now, cancellationToken).ConfigureAwait(false);
                await _audit.AppendAsync(
                    context,
                    IdentityAudit.Entry(origin, "user.password.reset", AuditOutcome.Success, user.Id.ToString(), w => w.WriteNumber("sessionsRevoked", revoked)),
                    cancellationToken).ConfigureAwait(false);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return new IdentityResult(IdentityStatus.Succeeded);
    }

    /// <summary>Ends every session of <paramref name="userId"/> except <paramref name="keep"/>; returns how many were active.</summary>
    internal static async Task<int> RevokeSessionsAsync(ConfigDbContext context, UserId userId, SessionId? keep, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sessions = await context.Sessions
            .Where(s => s.UserId == userId && s.RevokedAt == null && (keep == null || s.Id != keep))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var session in sessions)
        {
            session.RevokedAt = now;
        }

        return sessions.Count;
    }

    /// <summary>
    /// True if <paramref name="id"/> is an enabled Owner and no other enabled Owner exists: removing their role, disabling or
    /// deleting them would lock everyone out of user management. Call inside the write transaction.
    /// </summary>
    internal static async Task<bool> WouldLeaveNoOwnerAsync(ConfigDbContext context, UserId id, CancellationToken cancellationToken)
    {
        var isEnabledOwner = await context.Users.AnyAsync(
            u => u.Id == id && !u.Disabled && context.RoleAssignments.Any(r => r.UserId == id && r.Role == Role.Owner), cancellationToken).ConfigureAwait(false);
        if (!isEnabledOwner)
        {
            return false;
        }

        return !await context.RoleAssignments.AnyAsync(
            r => r.Role == Role.Owner && r.UserId != id && context.Users.Any(u => u.Id == r.UserId && !u.Disabled), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The checks <see cref="CreateAsync"/> and the setup flow share: a valid username, display name, roles and password.</summary>
    internal string? CheckNewUser(CreateUserRequest request, out string username, out string displayName, out List<Role> roles)
    {
        username = (request.Username ?? string.Empty).Trim();
        displayName = username;
        roles = [.. (request.Roles ?? []).Distinct()];
        if (!UsernamePattern.IsMatch(username))
        {
            return "The username must be 3 to 64 characters: letters, digits and . _ - @ +";
        }

        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            displayName = IdentityMapping.Clean(request.DisplayName, MaxDisplayNameLength) ?? username;
        }

        return RolesProblem(roles) ?? _policy.Check(request.Password);
    }

    private static string? RolesProblem(List<Role> roles) =>
        roles.Count == 0 ? "A user needs at least one role." : roles.Exists(r => !Enum.IsDefined(r)) ? "Unknown role." : null;

    private async Task<UserResult> PermitThenAsync(CommandOrigin origin, UserId id, string action, Func<Task<UserResult>> run, CancellationToken cancellationToken)
    {
        if (!await _permissions.IsAllowedAsync(origin, Permission.UsersWrite, cancellationToken).ConfigureAwait(false))
        {
            return await DeniedUserAsync(origin, action, id).ConfigureAwait(false);
        }

        return await run().ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the user in a write transaction, lets <paramref name="change"/> edit it, and saves it with its audit event, unless the
    /// change refuses (nothing is saved then).
    /// </summary>
    private async Task<UserResult> MutateAsync(
        CommandOrigin origin,
        UserId id,
        string action,
        Func<ConfigDbContext, UserRow, DateTimeOffset, Task<IdentityStatus>> change,
        Action<System.Text.Json.Utf8JsonWriter>? details,
        CancellationToken cancellationToken)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var user = await LoadAsync(context, id, cancellationToken).ConfigureAwait(false);
                if (user is null)
                {
                    return new UserResult(IdentityStatus.NotFound);
                }

                var now = _time.GetUtcNow();
                var status = await change(context, user, now).ConfigureAwait(false);
                if (status != IdentityStatus.Succeeded)
                {
                    return new UserResult(status);
                }

                await _audit.AppendAsync(context, IdentityAudit.Entry(origin, action, AuditOutcome.Success, id.ToString(), details), cancellationToken).ConfigureAwait(false);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new UserResult(IdentityStatus.Succeeded, await SummaryAsync(context, user, cancellationToken).ConfigureAwait(false));
            }
        }
    }

    /// <summary>Audits a refusal (the actor and channel, the target when there is one) and answers it.</summary>
    private async Task<UserResult> DeniedUserAsync(CommandOrigin origin, string action, UserId? target)
    {
        await AuditDenialAsync(origin, action, target).ConfigureAwait(false);
        return new UserResult(IdentityStatus.Denied);
    }

    /// <summary><see cref="DeniedUserAsync"/> for an operation that answers with an <see cref="IdentityResult"/>.</summary>
    private async Task<IdentityResult> DeniedIdentityAsync(CommandOrigin origin, string action, UserId? target)
    {
        await AuditDenialAsync(origin, action, target).ConfigureAwait(false);
        return new IdentityResult(IdentityStatus.Denied);
    }

    private Task AuditDenialAsync(CommandOrigin origin, string action, UserId? target) =>
        _audit.TryAppendAsync(_contexts, IdentityAudit.Entry(origin, action, AuditOutcome.Denied, target?.ToString()));

    private static Task<UserRow?> LoadAsync(ConfigDbContext context, UserId id, CancellationToken cancellationToken) =>
        context.Users.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);

    private async Task<UserSummary> SummaryAsync(ConfigDbContext context, UserRow user, CancellationToken cancellationToken) =>
        IdentityMapping.ToSummary(
            user,
            await context.RoleAssignments.AsNoTracking().Where(r => r.UserId == user.Id).Select(r => r.Role).ToListAsync(cancellationToken).ConfigureAwait(false),
            _time.GetUtcNow());

    private async Task DeleteTotpSecretAsync(SecretId? secret)
    {
        if (secret is not { } id || _totp is null)
        {
            return;
        }

        try
        {
            await _totp.DeleteAsync(id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Security.Cryptography.CryptographicException or DbUpdateException)
        {
            LogTotpDeleteFailed(ex, id);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The user is gone but their TOTP secret {Secret} could not be deleted from the vault.")]
    private partial void LogTotpDeleteFailed(Exception exception, SecretId secret);

    [GeneratedRegex("^[A-Za-z0-9._@+-]{3,64}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex UsernameRegex();
}
