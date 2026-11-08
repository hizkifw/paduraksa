using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Vault.Crypto;
using Paduraksa.Vault.Persistence;
using Paduraksa.Vault.Secrets;

namespace Paduraksa.Vault.Totp;

/// <summary>
/// <see cref="ITotpAuthenticator"/>: the shared secret is generated here, sealed like any other secret (kind
/// <see cref="SecretKind.TotpSecret"/>) and opened only for the duration of one verification. The one deliberate egress is the
/// provisioning URI that <see cref="BeginEnrollmentAsync"/> returns. Replay protection is the <see cref="TotpStateRow.LastStep"/>
/// column, advanced with a conditional update, so two simultaneous requests with the same code cannot both succeed, even from
/// two processes.
/// </summary>
internal sealed class TotpAuthenticator(VaultRuntime runtime, SecretReader reader) : ITotpAuthenticator
{
    public const int MaxNameLength = 128;

    public async ValueTask<TotpEnrollment> BeginEnrollmentAsync(string accountName, string issuer, CancellationToken cancellationToken)
    {
        RequireLabel(accountName, nameof(accountName));
        RequireLabel(issuer, nameof(issuer));
        if (issuer.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("The issuer must not contain ':'.", nameof(issuer));
        }

        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);

        using var secret = SecretBuffer.Allocate(TotpMath.SecretBytes);
        RandomNumberGenerator.Fill(secret.Fill);

        var now = runtime.Time.GetUtcNow();
        var row = new SecretRow
        {
            Id = SecretId.New(),
            Kind = SecretKind.TotpSecret,
            Label = $"TOTP for {accountName}",
            AllowedHosts = [],
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            // The seal and both inserts happen inside the transaction that checks the KEK against the database, like every seal.
            var transaction = await runtime.BeginSealingAsync(context, cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                row.Sealed = EnvelopeCipher.Seal(runtime.Kek, secret.Span, SecretReader.ContextOf(row, row.Version));
                context.Secrets.Add(row);
                context.TotpStates.Add(new TotpStateRow { SecretId = row.Id, Confirmed = false });
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return new TotpEnrollment(row.Id, ProvisioningUri(secret.Span, accountName, issuer));
    }

    public ValueTask<bool> ConfirmEnrollmentAsync(SecretId id, string code, CancellationToken cancellationToken) =>
        AcceptAsync(id, code, confirming: true, cancellationToken);

    public ValueTask<bool> VerifyAsync(SecretId id, string code, CancellationToken cancellationToken) =>
        AcceptAsync(id, code, confirming: false, cancellationToken);

    public async ValueTask<bool> DeleteAsync(SecretId id, CancellationToken cancellationToken)
    {
        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);
        var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            // The state row goes with the secret (cascade). Only a TOTP secret: this is not a way to delete a DNS token.
            return await context.Secrets
                .Where(s => s.Id == id && s.Kind == SecretKind.TotpSecret)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false) > 0;
        }
    }

    private async ValueTask<bool> AcceptAsync(SecretId id, string code, bool confirming, CancellationToken cancellationToken)
    {
        if (!TotpMath.TryParseCode(code, out var presented))
        {
            return false;
        }

        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        using var opened = await reader.OpenAsync(id, includePrevious: false, cancellationToken).ConfigureAwait(false);
        if (opened is null || opened.Row.Kind != SecretKind.TotpSecret)
        {
            return false;
        }

        var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var state = await context.TotpStates.AsNoTracking().SingleOrDefaultAsync(s => s.SecretId == id, cancellationToken).ConfigureAwait(false);
            if (state is null || state.Confirmed == confirming)
            {
                // No state, or the wrong phase: pending secrets verify nothing and confirmed ones cannot be confirmed again.
                return false;
            }

            var matched = TotpMath.MatchingStep(opened.Current.Span, presented, TotpMath.StepOf(runtime.Time.GetUtcNow()));
            if (matched < 0 || (state.LastStep is { } last && matched <= last))
            {
                return false;
            }

            var updated = await context.TotpStates
                .Where(s => s.SecretId == id && s.Confirmed == state.Confirmed && (s.LastStep == null || s.LastStep < matched))
                .ExecuteUpdateAsync(
                    u => u.SetProperty(s => s.LastStep, (long?)matched).SetProperty(s => s.Confirmed, true),
                    cancellationToken)
                .ConfigureAwait(false);
            return updated == 1;
        }
    }

    private static Uri ProvisioningUri(ReadOnlySpan<byte> secret, string accountName, string issuer)
    {
        var label = Uri.EscapeDataString(issuer) + ":" + Uri.EscapeDataString(accountName);
        return new Uri(
            $"otpauth://totp/{label}?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}"
            + $"&algorithm=SHA1&digits={TotpMath.Digits}&period={TotpMath.StepSeconds}",
            UriKind.Absolute);
    }

    private static void RequireLabel(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        if (value.Length > MaxNameLength || value.Any(char.IsControl))
        {
            throw new ArgumentException($"At most {MaxNameLength} characters, no control characters.", parameter);
        }
    }
}
