using Paduraksa.Core.Model;

namespace Paduraksa.Core.Vault;

/// <summary>Opens <see cref="IMacKey"/> handles for secrets in the vault.</summary>
/// <remarks>
/// A MAC key is a <see cref="ISecretStore"/> secret of a MAC kind (<see cref="SecretKind.EabHmacKey"/>,
/// <see cref="SecretKind.TsigKey"/>, <see cref="SecretKind.HmacSigningKey"/>) whose algorithm was fixed when it was stored. For
/// ACME external account binding the user pastes the CA's key once; the API layer base64url-decodes it into
/// <see cref="ISecretStore.PutAsync"/> and from then on only a handle exists.
/// </remarks>
public interface IMacKeySource
{
    /// <summary>
    /// Opens a handle for the secret <paramref name="id"/>, expecting it to be of kind <paramref name="expected"/>. Refused
    /// (<see langword="null"/>) if there is no such secret, if its kind is not <paramref name="expected"/>, or if it is not
    /// a MAC kind: a DNS token cannot be opened as an HMAC key, nor the other way round.
    /// </summary>
    ValueTask<IMacKey?> OpenAsync(SecretId id, SecretKind expected, CancellationToken cancellationToken);
}
