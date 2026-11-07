using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Paduraksa.Acme;

/// <summary>Challenge types and the values a challenge needs published (RFC 8555 §8).</summary>
public static partial class AcmeChallenges
{
    public const string Http01 = "http-01";
    public const string Dns01 = "dns-01";

    /// <summary>The label DNS-01 puts in front of the validated name.</summary>
    public const string Dns01Label = "_acme-challenge";

    /// <summary>The path an HTTP-01 response is served at.</summary>
    public const string Http01PathPrefix = "/.well-known/acme-challenge/";

    /// <summary>
    /// The key authorization (RFC 8555 §8.1): <c>token || "." || thumbprint</c>. It is the body of the HTTP-01 response and
    /// the input of <see cref="Dns01TxtValue"/>. The thumbprint is the account key's RFC 7638 thumbprint, which the vault
    /// reports in <c>AcmeAccountKeyInfo</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The token is not base64url (RFC 8555 §8.1 limits it to that alphabet).</exception>
    public static string KeyAuthorization(string token, string thumbprint)
    {
        ArgumentException.ThrowIfNullOrEmpty(thumbprint);
        if (!IsValidToken(token))
        {
            throw new ArgumentException("A challenge token must be a non-empty base64url string.", nameof(token));
        }

        return $"{token}.{thumbprint}";
    }

    /// <summary>The TXT record value for DNS-01 (RFC 8555 §8.4): <c>base64url(SHA-256(keyAuthorization))</c>.</summary>
    public static string Dns01TxtValue(string keyAuthorization)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyAuthorization);
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(keyAuthorization)));
    }

    /// <summary>The TXT record name for DNS-01: <c>_acme-challenge.</c> in front of the name, wildcard prefix removed.</summary>
    /// <exception cref="ArgumentException">The identifier is not a DNS name (DNS-01 cannot validate an IP address).</exception>
    public static string Dns01RecordName(AcmeIdentifier identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        if (identifier.Type != AcmeIdentifier.TypeDns)
        {
            throw new ArgumentException("DNS-01 only validates DNS identifiers.", nameof(identifier));
        }

        var name = identifier.IsWildcard ? identifier.Value[2..] : identifier.Value;
        return $"{Dns01Label}.{name}";
    }

    /// <summary>The URL path an HTTP-01 response is served at for <paramref name="token"/>.</summary>
    /// <exception cref="ArgumentException">The token is not base64url.</exception>
    public static string Http01Path(string token) =>
        IsValidToken(token)
            ? Http01PathPrefix + token
            : throw new ArgumentException("A challenge token must be a non-empty base64url string.", nameof(token));

    /// <summary>Whether <paramref name="token"/> is a non-empty string over the base64url alphabet.</summary>
    public static bool IsValidToken(string? token) => token is { Length: > 0 and <= 512 } && TokenPattern().IsMatch(token);

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
