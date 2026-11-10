namespace Paduraksa.Application.Config;

/// <summary>The stable <see cref="Contracts.Config.ConfigWarning.Code"/>s of the config compiler.</summary>
public static class ConfigWarningCodes
{
    /// <summary>A host with the Auto binding has names that no valid certificate covers.</summary>
    public const string CertificateAutoNoCoverage = "certificate.auto.noCoverage";

    /// <summary>A host's Specific binding names a certificate that is missing, expired, not yet valid, pending,
    /// failed or without a key in the vault. A command that <i>introduces</i> such a binding is invalid (<see
    /// cref="ConfigErrorCodes.HostCertificateUnusable"/>); one that finds it already there (the certificate expired
    /// since) only warns, so one bad certificate never blocks the rest.</summary>
    public const string CertificateSpecificUnusable = "certificate.specific.unusable";

    /// <summary>A host's Specific certificate doesn't cover some of the host's names (clients will see a name mismatch).</summary>
    public const string CertificateSpecificNoCoverage = "certificate.specific.noCoverage";

    /// <summary>The default certificate the config names is unusable (see <see cref="CertificateSpecificUnusable"/>);
    /// the inventory's fallback default, if any, serves instead.</summary>
    public const string CertificateDefaultUnusable = "certificate.default.unusable";

    /// <summary>An admin host set <c>forceHttps: false</c>; an admin host is always forced to HTTPS, so the setting is ignored.</summary>
    public const string AdminForceHttpsIgnored = "host.admin.forceHttpsIgnored";

    /// <summary>An admin host exists while <c>Paduraksa:Listen:AdminInternal</c> is off: it answers 503 until the listener is enabled.</summary>
    public const string AdminInternalDisabled = "host.admin.internalDisabled";

    /// <summary>An upstream with HTTPS destinations accepts any certificate (<c>tls.trust: any</c>).</summary>
    public const string UpstreamTlsUnverified = "upstream.tls.unverified";

    /// <summary>The proof-of-work gate is on for a host that also answers plain HTTP without forcing HTTPS, where its
    /// cookie cannot be marked <c>Secure</c>.</summary>
    public const string ProofOfWorkInsecureCookie = "proofOfWork.insecureCookie";
}
