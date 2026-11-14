using Microsoft.EntityFrameworkCore;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Core.Options;
using Paduraksa.Core.Vault;
using Paduraksa.Data;
using Paduraksa.Data.Identity;

namespace Paduraksa.Application.Identity;

/// <summary>How a sign-in step ended.</summary>
public enum LoginStatus
{
    /// <summary>Signed in: <see cref="LoginResult.Session"/> is a full session.</summary>
    Succeeded = 1,

    /// <summary>The password was right and the user has a second factor: <see cref="LoginResult.Session"/> is a partial session that only <see cref="LoginService.CompleteSecondFactorAsync"/> accepts.</summary>
    SecondFactorRequired = 2,

    /// <summary>
    /// Refused. The message is the same whether the user does not exist, the password is wrong, the account is locked or it is
    /// disabled: the response never tells which (the audit log does).
    /// </summary>
    InvalidCredentials = 3,

    /// <summary>The second-factor code or recovery code was wrong (or none, or both, was given).</summary>
    InvalidCode = 4,

    /// <summary>The partial session is unknown, expired, or its account is now locked or disabled: start again with the password.</summary>
    SessionExpired = 5,
}

/// <summary>The outcome of a sign-in step.</summary>
public sealed record LoginResult(LoginStatus Status, SessionGrant? Session = null);

/// <summary>
/// Signing in: username and password, then, if the user enrolled a second factor, a TOTP or recovery code. After
/// <see cref="IdentityOptions.MaxFailedAttempts"/> consecutive failures an account refuses sign-in for
/// <see cref="IdentityOptions.LockoutDuration"/>, whatever is presented. Every success, failure and lockout is audited (failures
/// under the system actor with the account as target, so an attacker's attempts never read as the user's own actions).
/// </summary>
/// <remarks>
/// <b>No account enumeration.</b> An unknown username costs a full password derivation against a throwaway hash, and every
/// refusal returns <see cref="LoginStatus.InvalidCredentials"/>. A password that is right for a locked or disabled account is
/// refused the same way. The cookie changes when the second factor completes (a partial session's value is never a full one).
/// </remarks>
public sealed class LoginService
{
    private readonly IDbContextFactory<ConfigDbContext> _contexts;
    private readonly TimeProvider _time;
    private readonly LiveOptions<IdentityOptions> _live;

    // Read on every use: the server settings may replace them while the process runs.
    private IdentityOptions Options => _live.Current;
    private readonly PasswordHasher _hasher;
    private readonly IdentityAudit _audit;
    private readonly SessionService _sessions;
    private readonly ITotpAuthenticator _totp;
    private readonly SignInWatch? _watch;

    internal LoginService(
        IDbContextFactory<ConfigDbContext> contexts,
        TimeProvider time,
        LiveOptions<IdentityOptions> options,
        PasswordHasher hasher,
        IdentityAudit audit,
        SessionService sessions,
        ITotpAuthenticator totp,
        SignInWatch? watch)
    {
        _contexts = contexts;
        _time = time;
        _live = options;
        _hasher = hasher;
        _audit = audit;
        _sessions = sessions;
        _totp = totp;
        _watch = watch;
    }

    /// <summary>Step one: the username and password.</summary>
    public async Task<LoginResult> LoginAsync(string username, string password, ClientContext client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var name = (username ?? string.Empty).Trim();
        if (name.Length is 0 or > IdentityConfiguration.MaxUsernameLength || PasswordPolicy.IsEmptyOrTooLong(password))
        {
            _hasher.VerifyUnknown(password ?? string.Empty);
            return new LoginResult(LoginStatus.InvalidCredentials);
        }

        var system = client.ToOrigin(Actor.System);
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Username == name, cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                _hasher.VerifyUnknown(password);
                // Not the typed name: people sometimes type their password into the username box, and the audit chain is permanent.
                await _audit.TryAppendAsync(_contexts, IdentityAudit.Entry(system, "auth.login", AuditOutcome.Denied, null, w => w.WriteString("reason", "unknown_user"))).ConfigureAwait(false);
                return new LoginResult(LoginStatus.InvalidCredentials);
            }

            // The attempt is counted (and a locked account refused) before anything is derived, in one write transaction: parallel
            // guesses queue up here, so no more than the allowed number of them are ever evaluated.
            var attempt = await ReserveAsync(context, system, user.Id, "auth.login", cancellationToken).ConfigureAwait(false);
            if (!attempt.Granted)
            {
                _hasher.VerifyUnknown(password); // a refusal takes as long as any other
                return new LoginResult(LoginStatus.InvalidCredentials);
            }

            var check = _hasher.Verify(password, user.PasswordHash);
            if (!check.Matches)
            {
                await RecordFailureAsync(context, system, user.Id, "auth.login", "wrong_password", attempt, revokeSessions: false, cancellationToken).ConfigureAwait(false);
                return new LoginResult(LoginStatus.InvalidCredentials);
            }

            var rehash = check.NeedsRehash ? _hasher.Hash(password) : null;

            // Read before the write transaction opens: it must not hold the writer lock for the audit lookup. A user without a second factor completes here.
            var newAddress = !user.TotpEnabled && _watch is not null && await _watch.IsNewAddressAsync(context, user.Id, client.ClientIp, cancellationToken).ConfigureAwait(false);
            context.ChangeTracker.Clear();
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                // Everything that decides the outcome is read again inside the write lock: the row read above may be stale.
                var row = await context.Users.SingleOrDefaultAsync(u => u.Id == user.Id, cancellationToken).ConfigureAwait(false);
                var now = _time.GetUtcNow();
                if (row is null || row.Disabled || (IdentityMapping.IsLocked(row, now) && row.LockedUntil != attempt.Engaged))
                {
                    await _audit.AppendAsync(
                        context,
                        IdentityAudit.Entry(
                            system,
                            "auth.login",
                            AuditOutcome.Denied,
                            user.Id.ToString(),
                            w => w.WriteString("reason", row is null ? "deleted" : row.Disabled ? "disabled" : "locked")),
                        cancellationToken).ConfigureAwait(false);
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new LoginResult(LoginStatus.InvalidCredentials);
                }

                var partial = row.TotpEnabled;
                if (!partial)
                {
                    IdentityMapping.ClearFailures(row);
                }
                else if (attempt.Engaged is not null && row.LockedUntil == attempt.Engaged)
                {
                    // The right password must not lock its own owner out of the second step; the count stays one short of the limit,
                    // and a password alone never earns fresh second-factor guesses.
                    row.LockedUntil = null;
                    row.FailedAttempts = Options.MaxFailedAttempts - 1;
                }

                if (rehash is not null)
                {
                    row.PasswordHash = rehash;
                }

                await SessionService.PurgeAsync(context, now, cancellationToken).ConfigureAwait(false);
                var grant = _sessions.Add(context, user.Id, client, secondFactorCompleted: !partial, now);
                await _audit.AppendAsync(
                    context,
                    IdentityAudit.Entry(
                        client.ToOrigin(Actor.ForUser(user.Id)), "auth.login", AuditOutcome.Success, grant.Id.ToString(), w => w.WriteString("secondFactor", partial ? "pending" : "none")),
                    cancellationToken).ConfigureAwait(false);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                if (newAddress && !partial)
                {
                    _watch!.PublishNewAddress(user.Username, user.Id, client.ClientIp!);
                }

                return new LoginResult(partial ? LoginStatus.SecondFactorRequired : LoginStatus.Succeeded, grant);
            }
        }
    }

    /// <summary>
    /// Step two, for a user with a second factor: the partial session's cookie and either a TOTP <paramref name="code"/> or a
    /// <paramref name="recoveryCode"/> (exactly one). On success the partial session becomes a full one under a <b>new</b> cookie value.
    /// Wrong codes count toward the lockout, and a lockout here ends the user's sessions.
    /// </summary>
    public async Task<LoginResult> CompleteSecondFactorAsync(string? cookieValue, string? code, string? recoveryCode, ClientContext client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!SessionService.IsWellFormed(cookieValue))
        {
            return new LoginResult(LoginStatus.SessionExpired);
        }

        var hash = SessionService.HashOf(cookieValue!);
        var now = _time.GetUtcNow();
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var session = await context.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.TokenHash == hash, cancellationToken).ConfigureAwait(false);
            if (session is null || session.RevokedAt is not null || session.SecondFactorCompleted || now >= session.ExpiresAt || now >= session.CreatedAt + Options.PartialSessionLifetime)
            {
                return new LoginResult(LoginStatus.SessionExpired);
            }

            var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == session.UserId, cancellationToken).ConfigureAwait(false);
            if (user is not { Disabled: false, TotpEnabled: true, TotpSecretId: { } secret })
            {
                return new LoginResult(LoginStatus.SessionExpired);
            }

            if (string.IsNullOrWhiteSpace(code) == string.IsNullOrWhiteSpace(recoveryCode))
            {
                return new LoginResult(LoginStatus.InvalidCode);
            }

            var system = client.ToOrigin(Actor.System);
            var attempt = await ReserveAsync(context, system, user.Id, "auth.second_factor", cancellationToken).ConfigureAwait(false);
            if (!attempt.Granted)
            {
                return new LoginResult(LoginStatus.SessionExpired);
            }

            var usedRecovery = string.IsNullOrWhiteSpace(code);
            var accepted = usedRecovery
                ? await ConsumeRecoveryCodeAsync(context, user.Id, recoveryCode!, now, cancellationToken).ConfigureAwait(false)
                : await _totp.VerifyAsync(secret, TwoFactorService.NormalizeCode(code), cancellationToken).ConfigureAwait(false);
            if (!accepted)
            {
                await RecordFailureAsync(context, system, user.Id, "auth.second_factor", usedRecovery ? "wrong_recovery_code" : "wrong_code", attempt, revokeSessions: true, cancellationToken)
                    .ConfigureAwait(false);
                return new LoginResult(attempt.Engaged is not null ? LoginStatus.SessionExpired : LoginStatus.InvalidCode);
            }

            var (cookie, newHash) = SessionService.NewCookie();
            var newAddress = _watch is not null && await _watch.IsNewAddressAsync(context, user.Id, client.ClientIp, cancellationToken).ConfigureAwait(false);
            context.ChangeTracker.Clear();
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var finished = _time.GetUtcNow();
                var row = await context.Sessions.SingleOrDefaultAsync(s => s.Id == session.Id && !s.SecondFactorCompleted && s.RevokedAt == null, cancellationToken).ConfigureAwait(false);
                var userRow = await context.Users.SingleOrDefaultAsync(u => u.Id == user.Id && !u.Disabled && u.TotpEnabled, cancellationToken).ConfigureAwait(false);
                // The expiry is read again under the write lock: a partial session can end between the two reads.
                if (row is null || row.ExpiresAt <= finished || userRow is null || (IdentityMapping.IsLocked(userRow, finished) && userRow.LockedUntil != attempt.Engaged))
                {
                    return new LoginResult(LoginStatus.SessionExpired);
                }

                var expires = finished + Options.SessionLifetime;
                IdentityMapping.ClearFailures(userRow);
                row.TokenHash = newHash;
                row.SecondFactorCompleted = true;
                row.LastSeenAt = finished;
                row.ExpiresAt = expires;
                await _audit.AppendAsync(
                    context,
                    IdentityAudit.Entry(
                        client.ToOrigin(Actor.ForUser(user.Id)), "auth.second_factor", AuditOutcome.Success, row.Id.ToString(), w => w.WriteString("method", usedRecovery ? "recovery_code" : "totp")),
                    cancellationToken).ConfigureAwait(false);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                if (newAddress)
                {
                    _watch!.PublishNewAddress(user.Username, user.Id, client.ClientIp!);
                }

                return new LoginResult(LoginStatus.Succeeded, new SessionGrant(row.Id, cookie, expires));
            }
        }
    }

    /// <summary>Marks a recovery code used if it is one of the user's and unused: one atomic update, so it works once.</summary>
    private static async Task<bool> ConsumeRecoveryCodeAsync(ConfigDbContext context, UserId user, string typed, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var hash = TwoFactorService.HashRecoveryCode(typed);
        return await context.RecoveryCodes
            .Where(c => c.UserId == user && c.CodeHash == hash && c.UsedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.UsedAt, now), cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// What <see cref="ReserveAsync"/> decided. <paramref name="Engaged"/> is the lockout this very attempt switched on by being the
    /// last one allowed (that attempt may still succeed; a later sibling may not).
    /// </summary>
    private readonly record struct Attempt(bool Granted, DateTimeOffset? Engaged);

    /// <summary>Counts an attempt against the account <b>before</b> it is evaluated, in a write transaction of its
    /// own: refused if the account is locked (audited), otherwise the count goes up and reaching the limit locks the
    /// account for everyone after this attempt.</summary>
    private async Task<Attempt> ReserveAsync(ConfigDbContext context, CommandOrigin system, UserId id, string action, CancellationToken cancellationToken)
    {
        context.ChangeTracker.Clear();
        var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var row = await context.Users.SingleOrDefaultAsync(u => u.Id == id, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                return new Attempt(false, null);
            }

            var now = _time.GetUtcNow();
            if (IdentityMapping.IsLocked(row, now))
            {
                await _audit.AppendAsync(context, IdentityAudit.Entry(system, action, AuditOutcome.Denied, id.ToString(), w => w.WriteString("reason", "locked")), cancellationToken).ConfigureAwait(false);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new Attempt(false, null);
            }

            var engaged = IdentityMapping.RegisterFailure(row, now, Options) ? row.LockedUntil : null;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new Attempt(true, engaged);
        }
    }

    /// <summary>
    /// Audits a failed attempt (the count was taken up front) and, if it was the one that locked the account, the lockout. A lockout
    /// at the second factor ends the user's sessions (<paramref name="revokeSessions"/>): whoever holds the password is not to be trusted
    /// there. A lockout at the password stage ends nothing, so that someone who only knows a username cannot keep signing the owner out.
    /// </summary>
    private async Task RecordFailureAsync(
        ConfigDbContext context, CommandOrigin system, UserId id, string action, string reason, Attempt attempt, bool revokeSessions, CancellationToken cancellationToken)
    {
        context.ChangeTracker.Clear();
        var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await _audit.AppendAsync(context, IdentityAudit.Entry(system, action, AuditOutcome.Denied, id.ToString(), w => w.WriteString("reason", reason)), cancellationToken).ConfigureAwait(false);
            if (attempt.Engaged is { } until)
            {
                if (revokeSessions)
                {
                    await UserService.RevokeSessionsAsync(context, id, keep: null, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                }

                await _audit.AppendAsync(
                    context,
                    IdentityAudit.Entry(system, "auth.lockout", AuditOutcome.Denied, id.ToString(), w => w.WriteString("until", until.ToString("O", System.Globalization.CultureInfo.InvariantCulture))),
                    cancellationToken).ConfigureAwait(false);
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (attempt.Engaged is { } locked && _watch is not null)
        {
            var name = await context.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.Username).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (name is not null)
            {
                _watch.PublishLockout(name, id, locked);
            }
        }
    }
}
