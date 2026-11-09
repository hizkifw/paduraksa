namespace Paduraksa.Core.Vault;

/// <summary>
/// The JWS signature algorithms an ACME account key can use (RFC 8555 §6.2 requires a public-key algorithm and
/// forbids MACs and <c>none</c> for account keys). Persisted by the JOSE name (<see cref="JwsAlgorithmNames"/>), never by number.
/// </summary>
public enum JwsAlgorithm
{
    /// <summary>ECDSA P-256 with SHA-256 (RFC 7518 §3.4). The default for new accounts.</summary>
    ES256 = 0,

    /// <summary>ECDSA P-384 with SHA-384.</summary>
    ES384 = 1,

    /// <summary>RSASSA-PKCS1-v1_5 with SHA-256 (RFC 7518 §3.3). The key is RSA 2048.</summary>
    RS256 = 2,
}

/// <summary>The JOSE names of <see cref="JwsAlgorithm"/> values, as written in a JWS protected header's <c>alg</c> member and persisted.</summary>
public static class JwsAlgorithmNames
{
    /// <summary>The JOSE name of <paramref name="algorithm"/> (<c>ES256</c>, <c>ES384</c> or <c>RS256</c>).</summary>
    public static string ToName(this JwsAlgorithm algorithm) => algorithm switch
    {
        JwsAlgorithm.ES256 => "ES256",
        JwsAlgorithm.ES384 => "ES384",
        JwsAlgorithm.RS256 => "RS256",
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Undefined JWS algorithm."),
    };

    /// <summary>The algorithm with the JOSE name <paramref name="name"/>.</summary>
    /// <exception cref="FormatException"><paramref name="name"/> is not <c>ES256</c>, <c>ES384</c> or <c>RS256</c>.</exception>
    public static JwsAlgorithm Parse(string name) => name switch
    {
        "ES256" => JwsAlgorithm.ES256,
        "ES384" => JwsAlgorithm.ES384,
        "RS256" => JwsAlgorithm.RS256,
        _ => throw new FormatException($"'{name}' is not a JwsAlgorithm name."),
    };
}

/// <summary>A public JSON Web Key (RFC 7517) as ACME sends it in a <c>jwk</c> header or a new-account payload. Public
/// members only: the type has no field that could carry a private component. Values are base64url, as RFC 7518 §6
/// defines them.</summary>
public sealed record JsonWebKeyPublic(string Kty, string? Crv, string? X, string? Y, string? N, string? E);

/// <summary>What an ACME client may know about an account key: its algorithm and public parts.</summary>
/// <param name="Algorithm">The algorithm to put in each protected header's <c>alg</c>.</param>
/// <param name="Jwk">
/// The public key. A <c>jwk</c> header (rather than <c>kid</c>) is used only for <c>newAccount</c>, for revoking a
/// certificate with its own key, and for the inner JWS of <c>keyChange</c> (deferred); revoking with the account
/// key uses <c>kid</c> like every other request. It is also the payload of external account binding.
/// </param>
/// <param name="Thumbprint">
/// The RFC 7638 SHA-256 thumbprint of <paramref name="Jwk"/> (base64url). It is the second half of every key
/// authorization (RFC 8555 §8.1: <c>token || "." || thumbprint</c>), so the vault computes it with the canonical member
/// order and ACME does not have to.
/// </param>
public sealed record AcmeAccountKeyInfo(JwsAlgorithm Algorithm, JsonWebKeyPublic Jwk, string Thumbprint);

/// <summary>
/// A signed JWS in the flattened JSON serialization (RFC 7515 §7.2.2). Each member is the already base64url-encoded
/// string that goes into the request body unchanged: ACME writes <c>{"protected":...,"payload":...,"signature":...}</c> with
/// exactly these values.
/// </summary>
/// <param name="Protected">base64url of the protected header JSON the caller supplied.</param>
/// <param name="Payload">base64url of the payload; the empty string for an empty payload (POST-as-GET, RFC 8555 §6.3).</param>
/// <param name="Signature">
/// base64url of the signature over <c>ASCII(Protected || "." || Payload)</c>. For ES256/ES384 it is the fixed-width
/// <c>R || S</c> (IEEE P1363, 64 and 96 bytes), not a DER <c>ECDSA-Sig-Value</c> (RFC 7518 §3.4); for RS256 the PKCS#1
/// v1.5 signature.
/// </param>
public sealed record JwsFlattened(string Protected, string Payload, string Signature);
