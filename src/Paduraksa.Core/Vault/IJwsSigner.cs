using Paduraksa.Core.Model;

namespace Paduraksa.Core.Vault;

/// <summary>
/// ACME account keys: the vault generates the key, signs with it, and publishes only the public JWK.
/// </summary>
/// <remarks>
/// <para>
/// The vault does the only key operation: it base64url-encodes the header and payload ACME hands it, signs
/// <c>Protected || "." || Payload</c> and returns the three pieces. It also parses the protected header just far enough to
/// refuse one whose <c>alg</c> differs from the key's, so an account key can only produce JWS signatures it declares.
/// </para>
/// <para>
/// A signer cannot avoid being an oracle for whatever ACME sends it, but it signs nothing but JWS input for this
/// key type: it never exposes a raw "sign these bytes" operation, and account keys are used for no other purpose.
/// </para>
/// <para>
/// <b>Account key rollover</b> (RFC 8555 §7.3.5) is deferred: recovery from a lost or compromised account key is a new
/// account.
/// </para>
/// </remarks>
public interface IJwsSigner
{
    /// <summary>
    /// Generates the key for a new account (ES256 is the default) and returns its public parts. Fails with
    /// <see cref="InvalidOperationException"/> if <paramref name="id"/> already has a key.
    /// </summary>
    ValueTask<AcmeAccountKeyInfo> CreateAccountKeyAsync(
        AcmeAccountId id, JwsAlgorithm algorithm, CancellationToken cancellationToken);

    /// <summary>The public parts of the account's key, or <see langword="null"/> if it has none.</summary>
    ValueTask<AcmeAccountKeyInfo?> GetAccountKeyAsync(AcmeAccountId id, CancellationToken cancellationToken);

    /// <summary>
    /// Signs the JWS made of <paramref name="protectedHeader"/> (UTF-8 JSON) and <paramref name="payload"/> (raw bytes;
    /// empty for POST-as-GET) with the account key.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The account has no key.</exception>
    /// <exception cref="ArgumentException">The header is not a JSON object, or its <c>alg</c> is not the key's algorithm.</exception>
    ValueTask<JwsFlattened> SignAsync(
        AcmeAccountId id, ReadOnlyMemory<byte> protectedHeader, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

    /// <summary>Deletes the account key. Returns false if there was none.</summary>
    ValueTask<bool> DeleteAccountKeyAsync(AcmeAccountId id, CancellationToken cancellationToken);
}
