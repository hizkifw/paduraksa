using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Vault.Crypto;
using Paduraksa.Vault.Persistence;

namespace Paduraksa.Vault.DataProtection;

/// <summary>
/// <see cref="IDataProtectionKeyStore"/>: the admin app's ASP.NET Core Data Protection key ring, sealed in
/// <c>vault.db</c> under the KEK exactly like every other key. Nothing here logs an element, and the opened plaintext lives in
/// a pinned, zeroed <see cref="SecretBuffer"/> until it is turned into the one string Data Protection asks for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sealing.</b> Each element is sealed with the envelope cipher under the purpose <c>data-protection-key\n&lt;friendly name&gt;</c>,
/// with the row's typed ID and version in the AAD, inside the KEK-check sealing transaction (like every other seal). Editing
/// the friendly name, or moving the ciphertext to another row, makes the element fail to open instead of opening as another
/// key. A store under a name that already exists replaces that element and increments the version (which is in the AAD too),
/// which is how Data Protection would replace an element should it ever ask.
/// </para>
/// <para>
/// <b>The one egress.</b> <see cref="ReadAllAsync"/> returns the element XML because Data Protection must parse and use it
/// in-process; that is the sanctioned egress <see cref="DataProtectionKey"/> documents. No other capability opens these rows.
/// </para>
/// </remarks>
internal sealed partial class DataProtectionKeyStore(VaultRuntime runtime, ILogger<DataProtectionKeyStore> logger) : IDataProtectionKeyStore
{
    /// <summary>The longest friendly name accepted (Data Protection's names are short: a timestamp and a GUID).</summary>
    public const int MaxFriendlyNameLength = 256;

    /// <summary>The largest element accepted, in UTF-8 bytes. A real element is a few kilobytes; this only stops a runaway caller from filling the vault.</summary>
    public const int MaxElementBytes = 64 * 1024;

    /// <summary>
    /// The purpose bound into an element's AAD: the stable purpose name and the friendly name, so the name cannot be edited in
    /// the database without the element failing to open.
    /// </summary>
    public static string PurposeOf(string friendlyName) => "data-protection-key\n" + friendlyName;

    /// <summary>What an element is sealed to: its typed ID and friendly name in the purpose, its version in the AAD.</summary>
    public static SealContext ContextOf(DataProtectionKeyRow row) => new(row.Id.ToString(), PurposeOf(row.FriendlyName), row.Version);

    public async ValueTask<IReadOnlyList<DataProtectionKey>> ReadAllAsync(CancellationToken cancellationToken)
    {
        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);

        var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var rows = await context.DataProtectionKeys
                .AsNoTracking()
                .OrderBy(r => r.CreatedAt)
                .ThenBy(r => r.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var keys = new List<DataProtectionKey>(rows.Count);
            foreach (var row in rows)
            {
                try
                {
                    using var element = EnvelopeCipher.Open(runtime.Kek, row.Sealed, ContextOf(row));
                    keys.Add(new DataProtectionKey(row.FriendlyName, Encoding.UTF8.GetString(element.Span)));
                }
                catch (CryptographicException ex)
                {
                    // One damaged row (a partial rotation, a flipped bit) must not take the whole ring down: Data
                    // Protection can still use the others, and a new key replaces the unreadable one on its next publish.
                    // The row's ID and name are metadata; the element itself is never logged.
                    LogUnreadable(logger, row.Id, row.FriendlyName, ex);
                }
            }

            return keys;
        }
    }

    public async ValueTask StoreAsync(string friendlyName, string keyXml, CancellationToken cancellationToken)
    {
        RequireFriendlyName(friendlyName);
        RequireElement(keyXml);

        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);

        var now = runtime.Time.GetUtcNow();
        var bytes = Encoding.UTF8.GetBytes(keyXml);
        try
        {
            var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                // The seal happens inside the transaction that checks the database still belongs to this process's KEK.
                var transaction = await runtime.BeginSealingAsync(context, cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    var row = await context.DataProtectionKeys
                        .SingleOrDefaultAsync(r => r.FriendlyName == friendlyName, cancellationToken)
                        .ConfigureAwait(false);
                    if (row is null)
                    {
                        row = new DataProtectionKeyRow
                        {
                            Id = DataProtectionKeyId.New(),
                            FriendlyName = friendlyName,
                            Version = 1,
                            CreatedAt = now,
                        };
                        context.DataProtectionKeys.Add(row);
                    }
                    else
                    {
                        row.Version++;
                    }

                    row.UpdatedAt = now;
                    row.Sealed = EnvelopeCipher.Seal(runtime.Kek, bytes, ContextOf(row));
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    LogSealed(logger, row.FriendlyName, row.Id);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void RequireFriendlyName(string friendlyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(friendlyName);
        if (friendlyName.Length > MaxFriendlyNameLength || friendlyName.Any(char.IsControl))
        {
            throw new ArgumentException($"At most {MaxFriendlyNameLength} characters, no control characters.", nameof(friendlyName));
        }
    }

    private static void RequireElement(string keyXml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyXml);
        if (Encoding.UTF8.GetByteCount(keyXml) > MaxElementBytes)
        {
            throw new ArgumentException($"The key element is at most {MaxElementBytes} bytes.", nameof(keyXml));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Data Protection key {FriendlyName} sealed in the vault ({KeyId}).")]
    private static partial void LogSealed(ILogger logger, string friendlyName, DataProtectionKeyId keyId);

    [LoggerMessage(Level = LogLevel.Error, Message = "The Data Protection key {FriendlyName} ({KeyId}) can't be opened, so it is left out of the ring; the other elements still load.")]
    private static partial void LogUnreadable(ILogger logger, DataProtectionKeyId keyId, string friendlyName, Exception exception);
}
