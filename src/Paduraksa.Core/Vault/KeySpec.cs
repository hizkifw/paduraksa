namespace Paduraksa.Core.Vault;

/// <summary>
/// The kind of key the vault generates for a certificate. Persisted by its stable name (<see cref="VaultNames"/>), never
/// by number. <see cref="EcdsaP256"/> is the zero value and so the default.
/// </summary>
public enum KeySpec
{
    /// <summary>ECDSA on NIST P-256. The default: small, fast, accepted by every public CA and TLS stack.</summary>
    EcdsaP256 = 0,

    /// <summary>ECDSA on NIST P-384.</summary>
    EcdsaP384 = 1,

    /// <summary>RSA 2048. For clients or CAs that cannot do ECDSA.</summary>
    Rsa2048 = 2,

    /// <summary>RSA 3072.</summary>
    Rsa3072 = 3,

    /// <summary>RSA 4096.</summary>
    Rsa4096 = 4,
}
