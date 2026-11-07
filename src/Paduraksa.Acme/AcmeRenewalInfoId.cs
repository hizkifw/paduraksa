using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Paduraksa.Core.Vault;

namespace Paduraksa.Acme;

/// <summary>The certificate identifier of ARI (RFC 9773 §4.1).</summary>
public static partial class AcmeRenewalInfoId
{
    /// <summary>
    /// <c>base64url(AKI keyIdentifier) "." base64url(serial)</c>, where the serial is the DER INTEGER's content bytes (a
    /// leading <c>00</c> kept), both without padding.
    /// </summary>
    /// <param name="authorityKeyIdentifier">The keyIdentifier of the Authority Key Identifier extension, as hex.</param>
    /// <param name="serialNumber">The serial number's content bytes as hex (<see cref="CertificateInfo.SerialNumber"/>).</param>
    /// <exception cref="ArgumentException">Either value is not an even-length hex string.</exception>
    public static string Create(string authorityKeyIdentifier, string serialNumber)
    {
        ArgumentException.ThrowIfNullOrEmpty(authorityKeyIdentifier);
        ArgumentException.ThrowIfNullOrEmpty(serialNumber);
        try
        {
            return $"{Base64Url.EncodeToString(Convert.FromHexString(authorityKeyIdentifier))}.{Base64Url.EncodeToString(Convert.FromHexString(serialNumber))}";
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The authority key identifier and the serial number must be hex.", ex);
        }
    }

    /// <summary>The ID of a certificate the vault reported.</summary>
    /// <exception cref="ArgumentException">The certificate has no authority key identifier, so ARI cannot name it.</exception>
    public static string Create(CertificateInfo certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.AuthorityKeyIdentifier is { } aki
            ? Create(aki, certificate.SerialNumber)
            : throw new ArgumentException("The certificate has no authority key identifier.", nameof(certificate));
    }

    /// <summary>Like <see cref="Create(CertificateInfo)"/>, but <see langword="false"/> instead of an exception when the certificate has no authority key identifier.</summary>
    public static bool TryCreate(CertificateInfo certificate, [NotNullWhen(true)] out string? id)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        id = certificate.AuthorityKeyIdentifier is null ? null : Create(certificate);
        return id is not null;
    }

    /// <summary>Whether <paramref name="id"/> has the shape of an ID: two non-empty base64url strings joined by a dot. Safe in a URL path.</summary>
    public static bool IsValid(string? id) => id is { Length: <= 1024 } && IdPattern().IsMatch(id);

    [GeneratedRegex(@"^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
