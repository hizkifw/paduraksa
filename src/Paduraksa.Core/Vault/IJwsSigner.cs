using Paduraksa.Core.Model;

namespace Paduraksa.Core.Vault;

/// <summary>
/// ACME account keys (PLAN §8.2, §9): the vault generates the key, signs with it, and publishes only the public JWK.
/// </summary>
/// <remarks>
/// <para>
/// <b>Division of labour.</b> <c>Paduraksa.Acme</c> builds the request: the protected header (<c>alg</c>, <c>nonce</c>,
/// <c>url</c> and <c>jwk</c> or <c>kid</c>, RFC 8555 §6.2) and the payload. It passes both as raw bytes to
/// <see cref="SignAsync"/>, and the vault does the only key operation: it base64url-encodes both parts, signs
/// <c>Protected || "." || Payload</c> and returns the three pieces, which ACME drops into the JSON body (<see cref="JwsFlattened"/>).
/// The vault also parses the protected header just far enough to refuse one whose <c>alg</c> differs from the key's, so
/// an account key can only produce JWS signatures it declares.
/// </para>
/// <para>
/// A signer cannot avoid being an oracle for whatever ACME sends it, but it signs nothing but JWS input for this
/// key type: it never exposes a raw "sign these bytes" operation, and account keys are used for no other purpose.
/// </para>
/// <para>
/// <b>External account binding</b> (RFC 8555 §7.3.4) is a second JWS, signed with an HMAC key rather than the account key.
/// It goes through <see cref="IMacKeySource"/>: ACME builds the inner JWS (<c>alg</c> HS256, the CA's <c>kid</c>, the account
/// JWK as payload) and has the MAC key sign its signing input.
/// </para>
/// <para>
/// <b>Account key rollover</b> (RFC 8555 §7.3.5) is deferred past v1: recovery from a lost or compromised account key is a
/// new account. The additive shape, when it comes, is <c>StageAccountKeyAsync</c>, <c>SignWithStagedAsync</c> (the inner JWS
/// with the new key) and <c>CommitStagedAccountKeyAsync</c>; nothing here needs to change for it.
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
