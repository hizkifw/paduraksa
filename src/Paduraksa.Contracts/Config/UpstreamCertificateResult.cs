namespace Paduraksa.Contracts.Config;

/// <summary>What fetching an HTTPS destination's certificate found.</summary>
public enum UpstreamCertificateOutcome
{
    /// <summary>The destination completed a TLS handshake; its certificate is in the result.</summary>
    Fetched,

    /// <summary>No connection within the timeout.</summary>
    Unreachable,

    /// <summary>A connection opened, but no TLS handshake completed (a plain-HTTP port, or a TLS failure).</summary>
    NoTls,

    /// <summary>The address is not an https URL; nothing was tried.</summary>
    Invalid,

    /// <summary>The caller may not probe (it lacks <c>config:write</c>); nothing was tried. Audited.</summary>
    Denied,
}

/// <summary>The certificate an HTTPS destination presented, for pinning it.</summary>
/// <param name="Sha256">The SHA-256 fingerprint of the certificate, 64 lowercase hex digits: the value a pin takes.</param>
/// <param name="Subject">The subject's distinguished name.</param>
/// <param name="Issuer">The issuer's distinguished name.</param>
/// <param name="NotBefore">Valid from.</param>
/// <param name="NotAfter">Valid until: a pin stops matching when the device renews its certificate.</param>
/// <param name="DnsNames">The DNS names in its subject alternative names.</param>
/// <param name="SelfSigned">Issued by itself (issuer equals subject).</param>
public sealed record UpstreamCertificateInfo(
    string Sha256, string Subject, string Issuer, DateTimeOffset NotBefore, DateTimeOffset NotAfter, IReadOnlyList<string> DnsNames, bool SelfSigned);

/// <summary>The certificate fetch's answer.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Certificate">The presented certificate, for <see cref="UpstreamCertificateOutcome.Fetched"/>.</param>
/// <param name="Message">Why it was not tried, for <see cref="UpstreamCertificateOutcome.Invalid"/>.</param>
public sealed record UpstreamCertificateResult(UpstreamCertificateOutcome Outcome, UpstreamCertificateInfo? Certificate = null, string? Message = null);
