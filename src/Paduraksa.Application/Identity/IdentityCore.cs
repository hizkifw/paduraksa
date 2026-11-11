using System.Collections.Frozen;
using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Paduraksa.Application.Audit;
using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Data.Identity;

namespace Paduraksa.Application.Identity;

/// <summary>Where a request came from, for the audit log and the session record.</summary>
public sealed record ClientContext(Channel Channel, IPAddress? ClientIp = null, string? UserAgent = null)
{
    /// <summary>The origin of a command by <paramref name="actor"/> from here.</summary>
    public CommandOrigin ToOrigin(Actor actor) => new(actor, Channel, ClientIp);
}

/// <summary>How an identity operation ended.</summary>
public enum IdentityStatus
{
    Succeeded = 1,

    /// <summary>The user, token or session does not exist (or is not the caller's to see).</summary>
    NotFound = 2,

    /// <summary>
    /// The origin lacks the permission. A refused <i>change</i> (create, update, revoke, enrol, password, ...) is audited as
    /// <see cref="Core.Model.AuditOutcome.Denied"/>; a refused read (listing users, sessions or tokens) is not, since reads are not audited at all.
    /// </summary>
    Denied = 3,

    /// <summary>The input was refused; <c>Problem</c> says why. Nothing changed.</summary>
    Invalid = 4,

    /// <summary>It clashes with something that exists (a username in use, a TOTP enrolment already active).</summary>
    Conflict = 5,

    /// <summary>It would leave no enabled Owner. Nothing changed.</summary>
    LastOwner = 6,

    /// <summary>A password or code was wrong. The message never says which part.</summary>
    InvalidCredentials = 7,
}

/// <summary>The outcome of an operation that returns no data.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Problem">Why it was refused (for <see cref="IdentityStatus.Invalid"/>), safe to show.</param>
public sealed record IdentityResult(IdentityStatus Status, string? Problem = null)
{
    public bool Succeeded => Status == IdentityStatus.Succeeded;
}

/// <summary>The outcome of an operation that returns a user.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="User">The user after the change.</param>
/// <param name="Problem">Why it was refused (for <see cref="IdentityStatus.Invalid"/>), safe to show.</param>
public sealed record UserResult(IdentityStatus Status, UserSummary? User = null, string? Problem = null)
{
    public bool Succeeded => Status == IdentityStatus.Succeeded;
}

/// <param name="Status">How it ended.</param>
/// <param name="Users">The users, oldest first; empty unless it succeeded.</param>
public sealed record UserListResult(IdentityStatus Status, IReadOnlyList<UserSummary> Users);

/// <summary>What an origin may do.</summary>
public interface IPermissionEvaluator
{
    /// <summary>
    /// Everything <paramref name="actor"/> may do right now. The system and the CLI may do everything; a user holds what their
    /// roles grant; a token holds its scopes narrowed to what its owner holds, and nothing once it is revoked or expired or
    /// its owner is disabled or gone.
    /// </summary>
    ValueTask<IReadOnlySet<Permission>> GetPermissionsAsync(Actor actor, CancellationToken cancellationToken);

    /// <summary>Whether the origin's actor holds <paramref name="permission"/>.</summary>
    ValueTask<bool> IsAllowedAsync(CommandOrigin origin, Permission permission, CancellationToken cancellationToken);
}

internal sealed class PermissionEvaluator(IDbContextFactory<ConfigDbContext> contexts, TimeProvider time) : IPermissionEvaluator
{
    public async ValueTask<IReadOnlySet<Permission>> GetPermissionsAsync(Actor actor, CancellationToken cancellationToken)
    {
        switch (actor.Kind)
        {
            case ActorKind.System or ActorKind.Cli:
                return RolePermissions.All;
            case ActorKind.User:
                var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                await using (context.ConfigureAwait(false))
                {
                    return await OfUserAsync(context, actor.UserId!.Value, cancellationToken).ConfigureAwait(false);
                }

            case ActorKind.Token:
                var tokenContext = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                await using (tokenContext.ConfigureAwait(false))
                {
                    var tokenId = actor.TokenId!.Value;
                    var token = await tokenContext.ApiTokens.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tokenId, cancellationToken).ConfigureAwait(false);
                    if (token is null || token.RevokedAt is not null || (token.ExpiresAt is { } expires && expires <= time.GetUtcNow()))
                    {
                        return Empty;
                    }

                    var owner = await OfUserAsync(tokenContext, token.UserId, cancellationToken).ConfigureAwait(false);
                    return RolePermissions.Narrow(owner, IdentityMapping.ParseScopes(token.Scopes));
                }

            default:
                return Empty;
        }
    }

    public async ValueTask<bool> IsAllowedAsync(CommandOrigin origin, Permission permission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return (await GetPermissionsAsync(origin.Actor, cancellationToken).ConfigureAwait(false)).Contains(permission);
    }

    private static readonly FrozenSet<Permission> Empty = FrozenSet<Permission>.Empty;

    /// <summary>The permissions of an enabled user's roles; none for a disabled or unknown user.</summary>
    internal static async Task<IReadOnlySet<Permission>> OfUserAsync(ConfigDbContext context, UserId id, CancellationToken cancellationToken)
    {
        var enabled = await context.Users.AsNoTracking().AnyAsync(u => u.Id == id && !u.Disabled, cancellationToken).ConfigureAwait(false);
        if (!enabled)
        {
            return Empty;
        }

        var roles = await context.RoleAssignments.AsNoTracking().Where(r => r.UserId == id).Select(r => r.Role).ToListAsync(cancellationToken).ConfigureAwait(false);
        return RolePermissions.Of(roles);
    }
}

/// <summary>Appends identity events to the audit chain, in the caller's transaction or in one of its own.</summary>
internal sealed partial class IdentityAudit(TimeProvider time, ILoggerFactory loggerFactory)
{
    private readonly AuditLog _log = new(time, loggerFactory.CreateLogger<AuditLog>());

    private readonly ILogger _logger = loggerFactory.CreateLogger<IdentityAudit>();

    /// <summary>Appends inside the caller's open transaction, with the change it records.</summary>
    public Task AppendAsync(ConfigDbContext context, AuditEntry entry, CancellationToken cancellationToken) =>
        _log.AppendAsync(context, entry, cancellationToken);

    /// <summary>
    /// Appends in a transaction of its own, for an outcome that changed nothing (a refusal), without the caller's cancellation;
    /// a failing database is logged, not thrown, so the outcome still reaches the caller.
    /// </summary>
    public async Task TryAppendAsync(IDbContextFactory<ConfigDbContext> contexts, AuditEntry entry)
    {
        try
        {
            var context = await contexts.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var transaction = await context.Database.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    await _log.AppendAsync(context, entry, CancellationToken.None).ConfigureAwait(false);
                    await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or System.Data.Common.DbException)
        {
            LogAuditFailed(ex, entry.Action, entry.Outcome);
        }
    }

    /// <summary>An entry for <paramref name="action"/> with an optional target and details object.</summary>
    public static AuditEntry Entry(CommandOrigin origin, string action, AuditOutcome outcome, string? target = null, Action<Utf8JsonWriter>? details = null) =>
        new(origin.Actor, origin.Channel, action, outcome)
        {
            Target = target,
            ClientIp = origin.ClientIp,
            Details = details is null ? null : AuditDetails.Json(details),
        };

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't write the audit event {Action} ({Outcome}); the outcome stands.")]
    private partial void LogAuditFailed(Exception exception, string action, AuditOutcome outcome);
}

/// <summary>Entity to DTO mapping and the small rules shared by the identity services.</summary>
internal static class IdentityMapping
{
    public static UserSummary ToSummary(UserRow user, IEnumerable<Role> roles, DateTimeOffset now) => new(
        user.Id,
        user.Username,
        user.DisplayName,
        [.. roles.Distinct().Order()],
        user.TotpEnabled,
        user.Disabled,
        user.LockedUntil is { } until && until > now,
        user.CreatedAt,
        user.UpdatedAt);

    public static ApiTokenSummary ToSummary(ApiTokenRow token) => new(
        token.Id,
        token.UserId,
        token.Name,
        [.. ParseScopes(token.Scopes)],
        token.ExpiresAt,
        token.CreatedAt,
        token.LastUsedAt,
        token.RevokedAt is not null);

    /// <summary>The scopes as stored: permission names in enum order, comma-separated.</summary>
    public static string FormatScopes(IEnumerable<Permission> scopes) =>
        string.Join(',', scopes.Distinct().Order().Select(s => s.ToName()));

    /// <summary>The permissions of a stored scope list; a name this build does not know is dropped (it grants nothing).</summary>
    public static IEnumerable<Permission> ParseScopes(string stored)
    {
        foreach (var name in stored.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (ModelNames.TryParsePermission(name, out var permission))
            {
                yield return permission;
            }
        }
    }

    /// <summary>Counts a failure against <paramref name="user"/>; returns true when it locked the account.</summary>
    public static bool RegisterFailure(UserRow user, DateTimeOffset now, IdentityOptions options)
    {
        if (user.LockedUntil is { } until && until <= now)
        {
            // The lockout has run out: start counting afresh.
            user.LockedUntil = null;
            user.FailedAttempts = 0;
        }

        user.FailedAttempts++;
        if (user.FailedAttempts < options.MaxFailedAttempts)
        {
            return false;
        }

        user.LockedUntil = now + options.LockoutDuration;
        user.FailedAttempts = 0;
        return true;
    }

    public static void ClearFailures(UserRow user)
    {
        user.FailedAttempts = 0;
        user.LockedUntil = null;
    }

    public static bool IsLocked(UserRow user, DateTimeOffset now) => user.LockedUntil is { } until && until > now;

    /// <summary>Truncates and strips control characters from client-supplied text before it is stored or audited.</summary>
    public static string? Clean(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var cleaned = new string([.. text.Where(c => !char.IsControl(c))]).Trim();
        return cleaned.Length == 0 ? null : cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength].TrimEnd();
    }
}
