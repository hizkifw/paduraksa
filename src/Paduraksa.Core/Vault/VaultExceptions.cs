namespace Paduraksa.Core.Vault;

/// <summary>Why an import was refused (<see cref="CertificateImportException.Reason"/>).</summary>
public enum CertificateImportFailure
{
    /// <summary>The upload is larger than <see cref="CertificateImportLimits.MaxBytes"/>.</summary>
    TooLarge = 0,

    /// <summary>The data is neither PEM nor PKCS#12, or it is malformed or exceeds the PKCS#12 loader limits.</summary>
    Unreadable = 1,

    /// <summary>The password is missing or wrong.</summary>
    WrongPassword = 2,

    /// <summary>The PEM key is in a format the vault does not read (only PKCS#8, plain or encrypted, is accepted).</summary>
    UnsupportedKeyFormat = 3,

    /// <summary>No certificate in the upload matches the private key, or there is no key at all.</summary>
    NoMatchingKey = 4,
}

/// <summary>
/// An uploaded certificate could not be imported. The vault maps every parsing, cryptographic and argument failure of
/// the import to this one type, so callers (and the API's error mapping) need to know only it and its reason.
/// </summary>
public sealed class CertificateImportException(CertificateImportFailure reason, string message) : Exception(message)
{
    /// <summary>Why the import was refused.</summary>
    public CertificateImportFailure Reason { get; } = reason;
}

/// <summary>Limits on an import (PLAN §9).</summary>
public static class CertificateImportLimits
{
    /// <summary>
    /// The largest upload accepted, in bytes. A certificate, its chain and a key are a few kilobytes; one megabyte is
    /// generous and keeps a hostile upload from costing memory or CPU. The vault stops reading at the cap and throws
    /// <see cref="CertificateImportException"/> with <see cref="CertificateImportFailure.TooLarge"/>.
    /// </summary>
    public const int MaxBytes = 1024 * 1024;
}

/// <summary>
/// The chain was attached with a staged-key token that is not the certificate's current one: the key was staged
/// again, replaced by an import or a re-issue, or deleted since. Nothing changed; start the issuance again.
/// </summary>
public sealed class StagedKeyMismatchException(string message) : InvalidOperationException(message);
