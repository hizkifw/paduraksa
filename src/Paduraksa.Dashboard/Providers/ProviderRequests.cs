using System.Buffers.Text;
using System.Security.Cryptography;

using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Providers;

/// <summary>
/// An ACME account as the form hands it to the page. The binding key is write-only: the form empties its field before the call and clears
/// this array after it; it is never shown, logged or stored outside the vault.
/// </summary>
public sealed record AcmeAccountSubmission(string DirectoryUrl, string? Email, bool TermsOfServiceAgreed, string? EabKid, char[] EabHmacKey, string? Label)
{
    public override string ToString() => $"{nameof(AcmeAccountSubmission)} {{ DirectoryUrl = {DirectoryUrl}, EabKid = {EabKid}, EabHmacKey = *** }}";
}

/// <summary>A DNS provider's credential as a form hands it over: the form empties its field before the call; the page clears the array after it.</summary>
public sealed record DnsCredentialSubmission(DnsProviderKind Kind, string Label, char[] Credential)
{
    public override string ToString() => $"{nameof(DnsCredentialSubmission)} {{ Kind = {Kind}, Label = {Label}, Credential = *** }}";
}

/// <summary>Turning a typed secret into the bytes the services store, and wiping every copy this side of the vault.</summary>
public static class SecretInput
{
    /// <summary>The UTF-8 bytes of <paramref name="text"/>; the caller zeroes them after use.</summary>
    public static byte[] Utf8(ReadOnlySpan<char> text)
    {
        var bytes = new byte[System.Text.Encoding.UTF8.GetByteCount(text)];
        System.Text.Encoding.UTF8.GetBytes(text, bytes);
        return bytes;
    }

    /// <summary>
    /// The binding key as the CA shows it (base64url, padding allowed) decoded, or null when it isn't base64url. The caller zeroes the result.
    /// </summary>
    public static byte[]? Base64UrlKey(ReadOnlySpan<char> text)
    {
        var trimmed = text.Trim().TrimEnd('=');
        if (trimmed.IsEmpty || !Base64Url.IsValid(trimmed))
        {
            return null;
        }

        var key = new byte[Base64Url.GetMaxDecodedLength(trimmed.Length)];
        if (!Base64Url.TryDecodeFromChars(trimmed, key, out var written))
        {
            CryptographicOperations.ZeroMemory(key);
            return null;
        }

        if (written == key.Length)
        {
            return key;
        }

        var exact = key[..written];
        CryptographicOperations.ZeroMemory(key);
        return exact;
    }
}
