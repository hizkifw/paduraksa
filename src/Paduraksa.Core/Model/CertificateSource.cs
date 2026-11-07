namespace Paduraksa.Core.Model;

/// <summary>Where a certificate came from (PLAN §5.1, §9). Persisted by name (<see cref="ModelNames"/>).</summary>
public enum CertificateSource
{
    /// <summary>Issued by an ACME CA (Let's Encrypt and the like).</summary>
    Acme = 1,

    /// <summary>Uploaded by an admin (PEM or PFX), including Cloudflare Origin CA certificates.</summary>
    Uploaded = 2,

    /// <summary>Self-signed by the vault.</summary>
    SelfSigned = 3,

    /// <summary>A leaf issued by the local CA.</summary>
    LocalCa = 4,

    /// <summary>The local CA's own root certificate (a normal certificate with a row and a downloadable chain).</summary>
    LocalCaRoot = 5,
}

/// <summary>
/// The lifecycle state of a certificate's metadata row (PLAN §5.1, §9.2). Persisted by name (<see cref="ModelNames"/>).
/// Expiry is not a state: it follows from <c>NotAfter</c> and the clock, so nothing has to flip a row when a
/// certificate runs out.
/// </summary>
public enum CertificateStatus
{
    /// <summary>Requested but not usable yet: no chain is attached (an ACME order in progress).</summary>
    Pending = 1,

    /// <summary>Has a chain and a key; usable while its validity period lasts.</summary>
    Active = 2,

    /// <summary>Issuance or renewal failed and nothing usable came of it (see the status message).</summary>
    Failed = 3,
}
