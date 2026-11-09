using System.Security.Cryptography;
using System.Text;

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

/// <summary>The outcome of starting a TOTP enrolment.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Enrollment">The provisioning URI, shown once.</param>
public sealed record TotpEnrollmentResult(IdentityStatus Status, TotpEnrollmentStarted? Enrollment = null);

/// <summary>The outcome of an operation that issues recovery codes.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Codes">The codes, shown once; the store keeps only hashes.</param>
public sealed record RecoveryCodesResult(IdentityStatus Status, RecoveryCodes? Codes = null);

/// <summary>
/// TOTP enrolment and recovery codes. The shared secret is generated, kept and checked by the vault
/// (<see cref="ITotpAuthenticator"/>); this service holds its reference in the user's record and drives the flow: begin
/// (the provisioning URI, once), confirm with a code from the authenticator (which turns the second factor on and returns the
/// recovery codes, once), disable. A user's second factor is on only after the confirmation, so an abandoned enrolment never
/// locks anyone out.
/// </summary>
/// <remarks>
/// Recovery codes are 16 random characters from an unambiguous alphabet (about 79 bits), written <c>xxxx-xxxx-xxxx-xxxx</c>; a
/// code is used once, and only its SHA-256 is stored. Ten are issued at enrolment, and asking for new ones replaces the old.
/// </remarks>
public sealed partial class TwoFactorService
{
    private const string RecoveryAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    private const int RecoveryCodeChars = 16;

    private readonly IDbContextFactory<ConfigDbContext> _contexts;
    private readonly TimeProvider _time;
    private readonly IdentityOptions _options;
    private readonly PasswordHasher _hasher;
    private readonly IdentityAudit _audit;
    private readonly IPermissionEvaluator _permissions;
    private readonly ITotpAuthenticator _totp;
    private readonly ILogger _logger;

    internal TwoFactorService(
        IDbContextFactory<ConfigDbContext> contexts,
        TimeProvider time,
        IdentityOptions options,
        PasswordHasher hasher,
        IdentityAudit audit,
        IPermissionEvaluator permissions,
        ITotpAuthenticator totp,
        ILogger<TwoFactorService> logger)
    {
        _contexts = contexts;
        _time = time;
        _options = options;
        _hasher = hasher;
        _audit = audit;
        _permissions = permissions;
        _totp = totp;
        _logger = logger;
    }

    /// <summary>
    /// Starts enrolling the calling user: the vault makes a secret and the result carries its provisioning URI (for the QR code),
    /// the one time it is shown. Needs the current password (a stolen session must not be able to bind its own authenticator). An
    /// earlier abandoned enrolment is discarded. Conflict if the second factor is already on.
    /// </summary>
    public async Task<TotpEnrollmentResult> BeginEnrollmentAsync(CommandOrigin origin, string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (origin.Actor.UserId is not { } id)
        {
            await DeniedAsync(origin, "user.totp.enroll").ConfigureAwait(false);
            return new TotpEnrollmentResult(IdentityStatus.Denied);
        }

        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var guard = await ReauthenticateAsync(context, origin, id, password, "user.totp.enroll", cancellationToken).ConfigureAwait(false);
            if (guard != IdentityStatus.Succeeded)
            {
                return new TotpEnrollmentResult(guard);
            }

            var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id && !u.Disabled, cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                return new TotpEnrollmentResult(IdentityStatus.NotFound);
            }

            if (user.TotpEnabled)
            {
                return new TotpEnrollmentResult(IdentityStatus.Conflict);
            }

            var enrollment = await _totp.BeginEnrollmentAsync(user.Username, _options.TotpIssuer, cancellationToken).ConfigureAwait(false);
            SecretId? previous;
            try
            {
                var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    var row = await context.Users.SingleAsync(u => u.Id == id, cancellationToken).ConfigureAwait(false);
                    previous = row.TotpSecretId;
                    row.TotpSecretId = enrollment.SecretId;
                    row.UpdatedAt = _time.GetUtcNow();
                    await _audit.AppendAsync(context, IdentityAudit.Entry(origin, "user.totp.enroll", AuditOutcome.Success, id.ToString()), cancellationToken).ConfigureAwait(false);
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                // Nothing recorded the new secret (the transaction failed or the request ended), so it would be unreachable: delete it
                // again, uncancellable like the certificate flows' rollbacks, before the original failure goes on.
                await DeleteSecretAsync(enrollment.SecretId).ConfigureAwait(false);
                throw;
            }

            await DeleteSecretAsync(previous).ConfigureAwait(false);
            return new TotpEnrollmentResult(IdentityStatus.Succeeded, new TotpEnrollmentStarted(enrollment.ProvisioningUri));
        }
    }

    /// <summary>
    /// Finishes enrolling with a code from the authenticator: turns the second factor on and returns ten recovery codes, once.
    /// A wrong code (<see cref="IdentityStatus.InvalidCredentials"/>) changes nothing and can be retried.
    /// </summary>
    public async Task<RecoveryCodesResult> ConfirmEnrollmentAsync(CommandOrigin origin, string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (origin.Actor.UserId is not { } id)
        {
            await DeniedAsync(origin, "user.totp.confirm").ConfigureAwait(false);
            return new RecoveryCodesResult(IdentityStatus.Denied);
        }

        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id && !u.Disabled, cancellationToken).ConfigureAwait(false);
            if (user is not { TotpSecretId: { } secret, TotpEnabled: false })
            {
                return new RecoveryCodesResult(user is null ? IdentityStatus.NotFound : IdentityStatus.Conflict);
            }

            if (!await _totp.ConfirmEnrollmentAsync(secret, NormalizeCode(code), cancellationToken).ConfigureAwait(false))
            {
                await _audit.TryAppendAsync(_contexts, IdentityAudit.Entry(origin, "user.totp.confirm", AuditOutcome.Denied, id.ToString())).ConfigureAwait(false);
                return new RecoveryCodesResult(IdentityStatus.InvalidCredentials);
            }

            var (display, rows) = NewRecoveryCodes(id);
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var row = await context.Users.SingleOrDefaultAsync(u => u.Id == id, cancellationToken).ConfigureAwait(false);
                if (row is null || row.TotpSecretId != secret || row.TotpEnabled)
                {
                    // A new enrolment began (or the user went) while the code was being checked: this secret is not the pending one any more.
                    return new RecoveryCodesResult(IdentityStatus.Conflict);
                }

                row.TotpEnabled = true;
                row.UpdatedAt = _time.GetUtcNow();
                await context.RecoveryCodes.Where(c => c.UserId == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                context.RecoveryCodes.AddRange(rows);
                await _audit.AppendAsync(context, IdentityAudit.Entry(origin, "user.totp.confirm", AuditOutcome.Success, id.ToString()), cancellationToken).ConfigureAwait(false);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return new RecoveryCodesResult(IdentityStatus.Succeeded, new RecoveryCodes(display));
        }
    }

    /// <summary>Replaces the calling user's recovery codes with ten new ones (shown once). Needs the current password.</summary>
    public async Task<RecoveryCodesResult> RegenerateRecoveryCodesAsync(CommandOrigin origin, string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (origin.Actor.UserId is not { } id)
        {
            await DeniedAsync(origin, "user.recovery_codes.regenerate").ConfigureAwait(false);
            return new RecoveryCodesResult(IdentityStatus.Denied);
        }

        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var guard = await ReauthenticateAsync(context, origin, id, password, "user.recovery_codes.regenerate", cancellationToken).ConfigureAwait(false);
            if (guard != IdentityStatus.Succeeded)
            {
                return new RecoveryCodesResult(guard);
            }

            if (!await context.Users.AnyAsync(u => u.Id == id && u.TotpEnabled, cancellationToken).ConfigureAwait(false))
            {
                return new RecoveryCodesResult(IdentityStatus.Conflict);
            }

            var (display, rows) = NewRecoveryCodes(id);
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                await context.RecoveryCodes.Where(c => c.UserId == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                context.RecoveryCodes.AddRange(rows);
                await _audit.AppendAsync(context, IdentityAudit.Entry(origin, "user.recovery_codes.regenerate", AuditOutcome.Success, id.ToString()), cancellationToken).ConfigureAwait(false);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return new RecoveryCodesResult(IdentityStatus.Succeeded, new RecoveryCodes(display));
        }
    }

    /// <summary>Turns the calling user's second factor off. Needs the current password (a wrong one counts toward the lockout).</summary>
    public async Task<IdentityResult> DisableAsync(CommandOrigin origin, string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (origin.Actor.UserId is not { } id)
        {
            await DeniedAsync(origin, "user.totp.disable").ConfigureAwait(false);
            return new IdentityResult(IdentityStatus.Denied);
        }

        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var guard = await ReauthenticateAsync(context, origin, id, password, "user.totp.disable", cancellationToken).ConfigureAwait(false);
            return guard == IdentityStatus.Succeeded
                ? await ClearAsync(context, origin, u => u.Id == id, revokeSessions: false, cancellationToken).ConfigureAwait(false)
                : new IdentityResult(guard);
        }
    }

    /// <summary>
    /// Turns off the second factor of the user named <paramref name="username"/> (the CLI's <c>admin disable-2fa</c>, for someone
    /// who lost their authenticator and recovery codes) and ends their sessions. Needs <see cref="Permission.UsersWrite"/>.
    /// </summary>
    public async Task<IdentityResult> DisableForUserAsync(CommandOrigin origin, string username, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(username);
        if (!await _permissions.IsAllowedAsync(origin, Permission.UsersWrite, cancellationToken).ConfigureAwait(false))
        {
            await DeniedAsync(origin, "user.totp.disable").ConfigureAwait(false);
            return new IdentityResult(IdentityStatus.Denied);
        }

        var name = username.Trim();
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            return await ClearAsync(context, origin, u => u.Username == name, revokeSessions: true, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The recovery codes as shown (ten, grouped) and the rows that store their hashes.</summary>
    internal static (IReadOnlyList<string> Display, List<RecoveryCodeRow> Rows) NewRecoveryCodes(UserId user)
    {
        var display = new List<string>(IdentityOptions.RecoveryCodeCount);
        var rows = new List<RecoveryCodeRow>(IdentityOptions.RecoveryCodeCount);
        for (var i = 0; i < IdentityOptions.RecoveryCodeCount; i++)
        {
            var raw = new string(RandomNumberGenerator.GetItems<char>(RecoveryAlphabet, RecoveryCodeChars));
            display.Add($"{raw[..4]}-{raw[4..8]}-{raw[8..12]}-{raw[12..]}");
            rows.Add(new RecoveryCodeRow { UserId = user, CodeHash = HashRecoveryCode(raw) });
        }

        return (display, rows);
    }

    /// <summary>The hash stored for a code as typed: case, dashes and spaces do not matter.</summary>
    internal static byte[] HashRecoveryCode(string typed) =>
        SHA256.HashData(Encoding.ASCII.GetBytes(new string([.. typed.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant)])));

    /// <summary>A TOTP code as typed: spaces and dashes (the way authenticators group digits) are dropped.</summary>
    internal static string NormalizeCode(string? code) => new([.. (code ?? string.Empty).Where(c => c is not (' ' or '-'))]);

    private async Task<IdentityResult> ClearAsync(
        ConfigDbContext context, CommandOrigin origin, System.Linq.Expressions.Expression<Func<UserRow, bool>> which, bool revokeSessions, CancellationToken cancellationToken)
    {
        SecretId? secret;
        var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var user = await context.Users.SingleOrDefaultAsync(which, cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                return new IdentityResult(IdentityStatus.NotFound);
            }

            secret = user.TotpSecretId;
            var now = _time.GetUtcNow();
            user.TotpSecretId = null;
            user.TotpEnabled = false;
            user.UpdatedAt = now;
            await context.RecoveryCodes.Where(c => c.UserId == user.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            if (revokeSessions)
            {
                await UserService.RevokeSessionsAsync(context, user.Id, keep: null, now, cancellationToken).ConfigureAwait(false);
            }

            await _audit.AppendAsync(context, IdentityAudit.Entry(origin, "user.totp.disable", AuditOutcome.Success, user.Id.ToString()), cancellationToken).ConfigureAwait(false);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await DeleteSecretAsync(secret).ConfigureAwait(false)
            ? new IdentityResult(IdentityStatus.Succeeded)
            : new IdentityResult(IdentityStatus.Succeeded, SecretNotDeleted);
    }

    /// <summary>
    /// Re-checks the user's password for a sensitive step. A wrong one is audited and counts toward the lockout, like a wrong
    /// password at sign-in: a stolen session must not become a way to guess it.
    /// </summary>
    private async Task<IdentityStatus> ReauthenticateAsync(
        ConfigDbContext context, CommandOrigin origin, UserId id, string password, string action, CancellationToken cancellationToken)
    {
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id && !u.Disabled, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return IdentityStatus.NotFound;
        }

        var now = _time.GetUtcNow();
        if (!IdentityMapping.IsLocked(user, now) && _hasher.Verify(password ?? string.Empty, user.PasswordHash).Matches)
        {
            return IdentityStatus.Succeeded;
        }

        var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var row = await context.Users.SingleAsync(u => u.Id == id, cancellationToken).ConfigureAwait(false);
            var locked = IdentityMapping.RegisterFailure(row, now, _options);
            if (locked)
            {
                // A proven session that keeps failing the password check is the suspicious one: it ends with all the others.
                await UserService.RevokeSessionsAsync(context, id, keep: null, now, cancellationToken).ConfigureAwait(false);
            }

            await _audit.AppendAsync(
                context,
                IdentityAudit.Entry(origin, action, AuditOutcome.Denied, id.ToString(), w => w.WriteBoolean("lockedOut", locked)),
                cancellationToken).ConfigureAwait(false);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        context.ChangeTracker.Clear();
        return IdentityStatus.InvalidCredentials;
    }

    private Task DeniedAsync(CommandOrigin origin, string action) =>
        _audit.TryAppendAsync(_contexts, IdentityAudit.Entry(origin, action, AuditOutcome.Denied));

    /// <summary>What a successful <see cref="IdentityResult"/> carries in its <c>Problem</c> when the vault could not delete the secret.</summary>
    public const string SecretNotDeleted = "The second factor is off, but its secret could not be deleted from the vault; it is unreferenced and harmless, delete it when the vault is reachable.";

    /// <summary>Deletes the secret from the vault; false (logged) if that failed. The user's record no longer names it either way.</summary>
    private async Task<bool> DeleteSecretAsync(SecretId? secret)
    {
        if (secret is not { } id)
        {
            return true;
        }

        try
        {
            await _totp.DeleteAsync(id, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or CryptographicException or DbUpdateException)
        {
            LogSecretDeleteFailed(ex, id);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't delete the TOTP secret {Secret} from the vault; it is unreferenced and harmless, delete it when the vault is reachable.")]
    private partial void LogSecretDeleteFailed(Exception exception, SecretId secret);
}
