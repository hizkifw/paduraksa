namespace Paduraksa.Core.Vault;

/// <summary>What a KEK rotation re-wrapped.</summary>
/// <param name="SecretsRewrapped">Secret values re-wrapped (a MAC secret's previous value counts as one more).</param>
/// <param name="KeysRewrapped">Keys re-wrapped: ACME account keys, certificate keys and the keys staged for a chain.</param>
public sealed record KekRotationResult(int SecretsRewrapped, int KeysRewrapped);

/// <summary>
/// Replaces the vault's key-encryption key (PLAN §8.3): the entry point of <c>paduraksa vault rotate-kek</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order, which the caller owns.</b> (1) Generate the new KEK (32 random bytes, standard base64) and <i>durably store it at
/// the source the vault will read at its next start</i> (<c>master.key</c>, the key file or the environment of the service),
/// keeping the old one. (2) Call <see cref="RotateKekAsync"/>. (3) Only after it returned, retire the old KEK. The rotation
/// commits in one transaction and is not resumable: if the process dies after the commit with the new KEK stored nowhere, the
/// vault cannot be opened any more; stored first, a crash at any point leaves a working vault under one of the two keys
/// (the check value in the database says which). The vault never stores the new KEK itself.
/// </para>
/// <para>
/// A rotation <b>re-wraps only</b>: the data-encryption keys change wrapping, the encrypted values do not change. Whoever
/// holds a copy of the old database <i>and</i> the old KEK can still decrypt that copy; to cut that off, replace the secrets
/// themselves. The old wraps are overwritten in <c>vault.db</c> and its write-ahead log (checkpointed after the commit).
/// </para>
/// <para>
/// The rotating process carries on under the new KEK in memory, and its MAC handles keep working throughout (they use cached
/// key material and do not wait for the rotation). A process that started with the old KEK must be restarted with the new one.
/// </para>
/// </remarks>
public interface IVaultKekRotation
{
    /// <summary>
    /// Re-wraps every data-encryption key under <paramref name="newMasterKey"/> (the text form: standard base64 of 32 bytes) in
    /// one transaction, then makes it the KEK in force. Nothing changes if it throws.
    /// </summary>
    /// <exception cref="ArgumentException">The key is not base64 of 32 bytes, or it is the one already in use.</exception>
    /// <exception cref="InvalidOperationException">The vault cannot start (see <see cref="IVaultLifecycle"/>).</exception>
    ValueTask<KekRotationResult> RotateKekAsync(ReadOnlyMemory<char> newMasterKey, CancellationToken cancellationToken);
}
