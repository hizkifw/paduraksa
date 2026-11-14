using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;
using Paduraksa.Core.Options;
using Paduraksa.Data;
using Paduraksa.Data.Identity;

namespace Paduraksa.Application.Identity;

/// <summary>A new session: the handle and the cookie value to give the browser. The value is shown once; only its hash is stored.</summary>
/// <param name="Id">The session.</param>
/// <param name="CookieValue">The opaque 256-bit random value (43 URL-safe characters).</param>
/// <param name="ExpiresAt">The absolute end of the session (of the partial session, until the second factor completes it).</param>
public sealed record SessionGrant(SessionId Id, string CookieValue, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"{nameof(SessionGrant)} {{ Id = {Id}, CookieValue = *** }}";
}

/// <summary>What a presented cookie amounts to.</summary>
public enum SessionStatus
{
    /// <summary>A live session whose second factor (if the user has one) is complete.</summary>
    Valid = 1,

    /// <summary>The password was right but the second factor is still owed: only the second-factor step is allowed.</summary>
    SecondFactorRequired = 2,

    /// <summary>Unknown, revoked, expired, idle too long, or its user is disabled or gone. The reasons are not told apart.</summary>
    Invalid = 3,
}

/// <summary>The user behind a session.</summary>
public sealed record SessionPrincipal(UserId UserId, SessionId SessionId, string Username, string DisplayName, IReadOnlyList<Role> Roles);

/// <summary>The outcome of <see cref="SessionService.ValidateAsync"/>.</summary>
public sealed record SessionValidation(SessionStatus Status, SessionPrincipal? Principal = null);

/// <param name="Status">How it ended.</param>
/// <param name="Sessions">The active sessions, newest first; empty unless it succeeded.</param>
public sealed record SessionListResult(IdentityStatus Status, IReadOnlyList<SessionSummary> Sessions);

/// <summary>
/// Admin sessions. The browser holds an opaque random 256-bit value; only its SHA-256 is stored, so neither a
/// database copy nor an audit log yields a usable cookie. No Data Protection layer wraps the value: it is already
/// unguessable and stored hashed, so a key ring would add rotation to manage and no security.
/// </summary>
/// <remarks>
/// A session ends at its absolute expiry or after <see cref="IdentityOptions.SessionIdleTimeout"/> without use, whichever is
/// first; its last-seen time is written at most once per <see cref="IdentityOptions.TouchInterval"/>, so validating is a read.
/// A session is <i>partial</i> until the second factor completes it: it proves a password and nothing else.
/// </remarks>
public sealed class SessionService
{
    /// <summary>The length of the cookie value: 32 random bytes in unpadded base64url.</summary>
    public const int CookieValueLength = 43;

    private const int SessionBytes = 32;

    private const int KeepRevokedFor = 7;

    private readonly IDbContextFactory<ConfigDbContext> _contexts;
    private readonly TimeProvider _time;
    private readonly LiveOptions<IdentityOptions> _live;

    // Read on every use: the server settings may replace them while the process runs.
    private IdentityOptions Options => _live.Current;
    private readonly IdentityAudit _audit;
    private readonly IPermissionEvaluator _permissions;
    private long _validations;

    internal SessionService(
        IDbContextFactory<ConfigDbContext> contexts, TimeProvider time, LiveOptions<IdentityOptions> options, IdentityAudit audit, IPermissionEvaluator permissions)
    {
        _contexts = contexts;
        _time = time;
        _live = options;
        _audit = audit;
        _permissions = permissions;
    }

    /// <summary>How many <see cref="ValidateAsync"/> calls this instance has seen. A test seam: a static file must cost none.</summary>
    internal long ValidationCount => Interlocked.Read(ref _validations);

    /// <summary>Checks a cookie value presented by a browser, and counts the use (to within the touch interval).</summary>
    public async Task<SessionValidation> ValidateAsync(string? cookieValue, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _validations);

        if (!IsWellFormed(cookieValue))
        {
            return new SessionValidation(SessionStatus.Invalid);
        }

        var hash = HashOf(cookieValue!);
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var session = await context.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.TokenHash == hash, cancellationToken).ConfigureAwait(false);
            return await CheckRowAsync(context, session, countAsUse: true, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Re-checks a session that was already authenticated by its cookie, by its ID: for a long-lived
    /// connection (a dashboard circuit) that holds no cookie of its own.</summary>
    /// <param name="id">The session the connection authenticated with.</param>
    /// <param name="countAsUse">
    /// True when the check is made for something the person did (it keeps an idle session alive, like a request with the cookie);
    /// false for a background re-check, so an open but unattended page still idles out.
    /// </param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public async Task<SessionValidation> CheckAsync(SessionId id, bool countAsUse, CancellationToken cancellationToken = default)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var session = await context.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken).ConfigureAwait(false);
            return await CheckRowAsync(context, session, countAsUse, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SessionValidation> CheckRowAsync(ConfigDbContext context, SessionRow? session, bool countAsUse, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (session is null || !IsLive(session, now))
        {
            return new SessionValidation(SessionStatus.Invalid);
        }

        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == session.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.Disabled)
        {
            return new SessionValidation(SessionStatus.Invalid);
        }

        var roles = await context.RoleAssignments.AsNoTracking().Where(r => r.UserId == user.Id).Select(r => r.Role).ToListAsync(cancellationToken).ConfigureAwait(false);
        var principal = new SessionPrincipal(user.Id, session.Id, user.Username, user.DisplayName, [.. roles.Order()]);
        if (!session.SecondFactorCompleted)
        {
            return new SessionValidation(SessionStatus.SecondFactorRequired, principal);
        }

        if (countAsUse && now - session.LastSeenAt >= Options.TouchInterval)
        {
            var id = session.Id;
            await context.Sessions.Where(s => s.Id == id && s.RevokedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastSeenAt, now), cancellationToken).ConfigureAwait(false);
        }

        return new SessionValidation(SessionStatus.Valid, principal);
    }

    /// <summary>
    /// The active sessions of <paramref name="owner"/>, newest first. A user may list their own; anyone else's needs
    /// <see cref="Permission.SessionsAll"/>. <paramref name="current"/> is marked in the list.
    /// </summary>
    public async Task<SessionListResult> ListAsync(CommandOrigin origin, UserId owner, SessionId? current = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!await MayAccessAsync(origin, owner, cancellationToken).ConfigureAwait(false))
        {
            return new SessionListResult(IdentityStatus.Denied, []);
        }

        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var now = _time.GetUtcNow();
            var sessions = await context.Sessions.AsNoTracking().Where(s => s.UserId == owner && s.RevokedAt == null).ToListAsync(cancellationToken).ConfigureAwait(false);
            return new SessionListResult(
                IdentityStatus.Succeeded,
                [.. sessions.Where(s => IsLive(s, now)).OrderByDescending(s => s.CreatedAt).Select(s => ToSummary(s, current))]);
        }
    }

    /// <summary>Ends one session: the caller's own, or anyone's with <see cref="Permission.SessionsAll"/>.</summary>
    public async Task<IdentityResult> RevokeAsync(CommandOrigin origin, SessionId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var session = await context.Sessions.SingleOrDefaultAsync(s => s.Id == id, cancellationToken).ConfigureAwait(false);
                if (session is null || (session.UserId != origin.Actor.UserId && !await _permissions.IsAllowedAsync(origin, Permission.SessionsAll, cancellationToken).ConfigureAwait(false)))
                {
                    // Someone else's session is, to this caller, one that does not exist.
                    return new IdentityResult(IdentityStatus.NotFound);
                }

                session.RevokedAt ??= _time.GetUtcNow();
                await _audit.AppendAsync(
                    context,
                    IdentityAudit.Entry(origin, "auth.session.revoke", AuditOutcome.Success, id.ToString(), w => w.WriteString("user", session.UserId.ToString())),
                    cancellationToken).ConfigureAwait(false);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return new IdentityResult(IdentityStatus.Succeeded);
    }

    /// <summary>Ends every session of the calling user except <paramref name="keep"/>.</summary>
    public async Task<IdentityResult> RevokeOthersAsync(CommandOrigin origin, SessionId keep, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (origin.Actor.UserId is not { } user)
        {
            await _audit.TryAppendAsync(_contexts, IdentityAudit.Entry(origin, "auth.session.revoke_others", AuditOutcome.Denied)).ConfigureAwait(false);
            return new IdentityResult(IdentityStatus.Denied);
        }

        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var revoked = await UserService.RevokeSessionsAsync(context, user, keep, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                await _audit.AppendAsync(
                    context,
                    IdentityAudit.Entry(origin, "auth.session.revoke_others", AuditOutcome.Success, user.ToString(), w => w.WriteNumber("revoked", revoked)),
                    cancellationToken).ConfigureAwait(false);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return new IdentityResult(IdentityStatus.Succeeded);
    }

    /// <summary>Ends the session behind a cookie (sign out). Unknown or dead cookies are ignored.</summary>
    public async Task LogoutAsync(string? cookieValue, ClientContext client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!IsWellFormed(cookieValue))
        {
            return;
        }

        var hash = HashOf(cookieValue!);
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var session = await context.Sessions.SingleOrDefaultAsync(s => s.TokenHash == hash && s.RevokedAt == null, cancellationToken).ConfigureAwait(false);
                if (session is null)
                {
                    return;
                }

                session.RevokedAt = _time.GetUtcNow();
                await _audit.AppendAsync(
                    context,
                    IdentityAudit.Entry(client.ToOrigin(Actor.ForUser(session.UserId)), "auth.logout", AuditOutcome.Success, session.Id.ToString()),
                    cancellationToken).ConfigureAwait(false);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>A fresh cookie value and the hash that is stored for it.</summary>
    internal static (string CookieValue, byte[] Hash) NewCookie()
    {
        var value = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SessionBytes));
        return (value, HashOf(value));
    }

    internal static byte[] HashOf(string cookieValue) => SHA256.HashData(Encoding.ASCII.GetBytes(cookieValue));

    /// <summary>Adds a session row for <paramref name="user"/> to <paramref name="context"/> (the caller saves) and returns the cookie for it.</summary>
    internal SessionGrant Add(ConfigDbContext context, UserId user, ClientContext client, bool secondFactorCompleted, DateTimeOffset now)
    {
        var (cookie, hash) = NewCookie();
        var expires = now + (secondFactorCompleted ? Options.SessionLifetime : Options.PartialSessionLifetime);
        var row = new SessionRow
        {
            Id = SessionId.New(),
            TokenHash = hash,
            UserId = user,
            ClientIp = client.ClientIp?.ToString(),
            UserAgent = IdentityMapping.Clean(client.UserAgent, IdentityConfiguration.MaxUserAgentLength),
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = expires,
            SecondFactorCompleted = secondFactorCompleted,
        };
        context.Sessions.Add(row);
        return new SessionGrant(row.Id, cookie, expires);
    }

    /// <summary>Deletes sessions that ended long enough ago that nobody will look at them (inside the caller's flow; best effort by the caller).</summary>
    internal static Task PurgeAsync(ConfigDbContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = now - TimeSpan.FromDays(KeepRevokedFor);
        return context.Sessions.Where(s => s.ExpiresAt < cutoff || (s.RevokedAt != null && s.RevokedAt < cutoff)).ExecuteDeleteAsync(cancellationToken);
    }

    // The stored expiry is the lifetime when the session began; the lifetime in force is checked too, so a shortened one (a server setting)
    // ends existing sessions at their next check. A session the second factor completed counts from its creation, so a shortening may end it
    // up to the partial lifetime early: the safe side.
    private bool IsLive(SessionRow session, DateTimeOffset now) =>
        session.RevokedAt is null && now < session.ExpiresAt && now < session.LastSeenAt + Options.SessionIdleTimeout
        && now < session.CreatedAt + (session.SecondFactorCompleted ? Options.SessionLifetime : Options.PartialSessionLifetime);

    private async Task<bool> MayAccessAsync(CommandOrigin origin, UserId owner, CancellationToken cancellationToken) =>
        origin.Actor.UserId == owner || await _permissions.IsAllowedAsync(origin, Permission.SessionsAll, cancellationToken).ConfigureAwait(false);

    private static SessionSummary ToSummary(SessionRow s, SessionId? current) =>
        new(s.Id, s.ClientIp, s.UserAgent, s.CreatedAt, s.LastSeenAt, s.ExpiresAt, s.SecondFactorCompleted, current == s.Id);

    internal static bool IsWellFormed(string? cookieValue)
    {
        if (cookieValue is not { Length: CookieValueLength })
        {
            return false;
        }

        foreach (var c in cookieValue)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}
