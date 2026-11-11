using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Paduraksa.Core.Vault;
using Paduraksa.Vault.Certificates;
using Paduraksa.Vault.Crypto;
using Paduraksa.Vault.DataProtection;
using Paduraksa.Vault.Keys;
using Paduraksa.Vault.Passwords;
using Paduraksa.Vault.Secrets;

namespace Paduraksa.Vault;

/// <summary>What <see cref="KekRotation.RotateAsync"/> re-wrapped, per kind of value.</summary>
/// <param name="Secrets">Secret values re-wrapped (a MAC secret's previous value counts too).</param>
/// <param name="AccountKeys">ACME account keys re-wrapped.</param>
/// <param name="CertificateKeys">Certificate keys re-wrapped (the local CA root's included).</param>
/// <param name="StagedKeys">Keys staged for a certificate chain re-wrapped.</param>
/// <param name="DataProtectionKeys">Data Protection key-ring elements re-wrapped.</param>
/// <param name="PasswordCredentials">Access-list password verifiers re-wrapped.</param>
internal sealed record KekRotated(int Secrets, int AccountKeys, int CertificateKeys, int StagedKeys, int DataProtectionKeys, int PasswordCredentials);

/// <summary>
/// KEK rotation: re-wraps every DEK in the database under a new KEK. Nothing is
/// re-encrypted but the DEKs, so the cost is one unwrap and one wrap per sealed value, and the plaintext is never produced.
/// </summary>
/// <remarks>
/// <para>
/// <b>All or nothing.</b> Every row and the KEK check value change in one transaction: it commits whole or not at all, so
/// the database is always under exactly one KEK. A value that cannot be re-wrapped (damaged, or under another KEK) aborts
/// the rotation with nothing changed. The commit itself is not cancellable (a cancellation arriving then could not undo it,
/// and the vault would be left holding the old KEK in memory over a database under the new one).
/// </para>
/// <para>
/// <b>Not this class's job, and the order that makes it safe:</b> making the new KEK durable. The caller stores the new KEK at
/// the source the next start reads <i>first</i>, then rotates, then retires the old KEK (<see cref="IVaultKekRotation"/>). A
/// process that started with the old KEK must be restarted with the new one: it refuses to start with the old one ("does not
/// match"). The rotating process itself carries on under the new KEK in memory.
/// </para>
/// <para>
/// <b>Old wraps do not linger.</b> The database runs with <c>secure_delete</c> (<see cref="Persistence.SecureDeleteInterceptor"/>),
/// so the rewritten rows leave no old wrapped DEK behind in the file's free space, and after the commit the write-ahead log is
/// checkpointed and truncated, so the pages that held the old wraps do not survive there either. (A checkpoint cannot truncate
/// while another connection is reading; that is logged and the old wraps then go with the log's next reset.) This
/// rotates the wrapping only: the encrypted values are untouched, so a copy of the old database plus the old KEK still opens.
/// </para>
/// </remarks>
internal sealed partial class KekRotation(VaultRuntime runtime, ILogger<KekRotation> logger) : IVaultKekRotation
{
    public async ValueTask<KekRotationResult> RotateKekAsync(ReadOnlyMemory<char> newMasterKey, CancellationToken cancellationToken)
    {
        Kek newKek;
        try
        {
            newKek = Kek.Parse(newMasterKey.Span);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException(ex.Message, nameof(newMasterKey));
        }

        try
        {
            var rotated = await RotateAsync(newKek, cancellationToken).ConfigureAwait(false);
            return new KekRotationResult(rotated.Secrets, rotated.AccountKeys, rotated.CertificateKeys, rotated.StagedKeys, rotated.DataProtectionKeys, rotated.PasswordCredentials);
        }
        catch
        {
            newKek.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Re-wraps everything under <paramref name="newKek"/>. On success the vault takes ownership of it (and zeroes the
    /// old one); on failure the caller still owns it.
    /// </summary>
    /// <exception cref="ArgumentException">The new KEK is the one already in use.</exception>
    public async Task<KekRotated> RotateAsync(Kek newKek, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(newKek);
        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);

        var old = runtime.Kek;
        if (old.Matches(newKek.CheckValue))
        {
            throw new ArgumentException("The new master key is the one already in use.", nameof(newKek));
        }

        var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var secrets = 0;
                foreach (var row in await context.Secrets.ToListAsync(cancellationToken).ConfigureAwait(false))
                {
                    row.Sealed = EnvelopeCipher.Rewrap(old, newKek, row.Sealed, SecretReader.ContextOf(row, row.Version));
                    secrets++;
                    if (row.PreviousSealed is { } previous && row.PreviousVersion is { } previousVersion)
                    {
                        row.PreviousSealed = EnvelopeCipher.Rewrap(old, newKek, previous, SecretReader.ContextOf(row, previousVersion));
                        secrets++;
                    }
                }

                var accountKeys = 0;
                foreach (var row in await context.AccountKeys.ToListAsync(cancellationToken).ConfigureAwait(false))
                {
                    row.Sealed = EnvelopeCipher.Rewrap(old, newKek, row.Sealed, JwsSigner.ContextOf(row.Id, row.Algorithm));
                    accountKeys++;
                }

                var certificateKeys = 0;
                foreach (var row in await context.CertificateKeys.ToListAsync(cancellationToken).ConfigureAwait(false))
                {
                    row.Sealed = EnvelopeCipher.Rewrap(old, newKek, row.Sealed, CertificateSeal.ContextOf(row.Id, row.IsLocalCaRoot, row.Version));
                    certificateKeys++;
                }

                var stagedKeys = 0;
                foreach (var row in await context.StagedKeys.ToListAsync(cancellationToken).ConfigureAwait(false))
                {
                    row.Sealed = EnvelopeCipher.Rewrap(old, newKek, row.Sealed, CertificateSeal.StagedContextOf(row.CertificateId, row.StagedKeyId, row.Names));
                    stagedKeys++;
                }

                // The admin app's Data Protection key ring: its elements are keys like any other.
                var dataProtectionKeys = 0;
                foreach (var row in await context.DataProtectionKeys.ToListAsync(cancellationToken).ConfigureAwait(false))
                {
                    row.Sealed = EnvelopeCipher.Rewrap(old, newKek, row.Sealed, DataProtectionKeyStore.ContextOf(row));
                    dataProtectionKeys++;
                }

                var passwordCredentials = await PasswordCredentialStore.RewrapAsync(context, old, newKek, cancellationToken).ConfigureAwait(false);

                var meta = await context.Meta.SingleAsync(cancellationToken).ConfigureAwait(false);
                meta.KekCheck = newKek.CheckValue;
                meta.UpdatedAt = runtime.Time.GetUtcNow();

                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                // Past this point the database is under the new KEK whatever happens, so the vault must follow: no cancellation.
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                runtime.ReplaceKek(newKek);
                await CheckpointAsync(context).ConfigureAwait(false);
                return new KekRotated(secrets, accountKeys, certificateKeys, stagedKeys, dataProtectionKeys, passwordCredentials);
            }
        }
    }

    /// <summary>Moves the write-ahead log into the database file and empties it, so the pages that held the old wraps are gone from it.</summary>
    private async Task CheckpointAsync(Persistence.VaultDbContext context)
    {
        try
        {
            await context.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            LogCheckpointFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The KEK rotation committed, but the write-ahead log could not be truncated; the old wrapped keys leave it with the log's next reset.")]
    private partial void LogCheckpointFailed(Exception exception);
}
