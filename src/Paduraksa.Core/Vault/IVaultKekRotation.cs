namespace Paduraksa.Core.Vault;

/// <summary>What a KEK rotation re-wrapped, counted per kind of value.</summary>
public sealed record KekRotationResult(int Secrets, int AccountKeys, int CertificateKeys, int StagedKeys, int DataProtectionKeys)
{
    /// <summary>Every key re-wrapped, of all kinds together (the secrets are not keys and are not counted here).</summary>
    public int Keys => AccountKeys + CertificateKeys + StagedKeys + DataProtectionKeys;
}

/// <summary>
/// Replaces the vault's key-encryption key: the entry point of <c>paduraksa vault rotate-kek</c>.
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
/// themselves. A process that started with the old KEK must be restarted with the new one.
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
