using Paduraksa.Core.Model;

namespace Paduraksa.Core.Vault;

/// <summary>
/// A certificate signing request the vault produced for a freshly generated key. The key stays in the vault; only
/// the request (public data) leaves.
/// </summary>
/// <remarks>Equality of <see cref="Der"/> is by buffer identity, not content, as for any <see cref="ReadOnlyMemory{T}"/>.</remarks>
/// <param name="StagedKey">Identifies the staged key. Present it to <c>ICertificateIssuance.AttachChainAsync</c> with the issued chain.</param>
/// <param name="Der">The PKCS#10 request, DER-encoded. ACME's finalize call sends it base64url-encoded (RFC 8555 §7.4).</param>
/// <param name="Pem">The same request as a PEM <c>CERTIFICATE REQUEST</c> block, for display and for CAs that want PEM.</param>
public sealed record CsrResult(StagedKeyId StagedKey, ReadOnlyMemory<byte> Der, string Pem);
