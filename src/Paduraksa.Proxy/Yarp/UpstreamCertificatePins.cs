using System.Buffers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Paduraksa.Proxy.Yarp;

/// <summary>
/// The SHA-256 fingerprints an upstream with pinned trust accepts. They travel to the HTTP client factory in the YARP
/// cluster's metadata (<see cref="MetadataKey"/>), because <c>HttpClientConfig</c> has no field for a validation callback.
/// </summary>
public sealed class UpstreamCertificatePins
{
    public const string MetadataKey = "paduraksa.tls.pinnedSha256";

    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdefABCDEF");

    private readonly HashSet<string> _pins;

    private UpstreamCertificatePins(HashSet<string> pins) => _pins = pins;

    /// <summary>A fingerprint as 64 lowercase hex digits; colons and the spacing tools print are dropped first.</summary>
    public static bool TryNormalize(string? text, out string fingerprint)
    {
        fingerprint = string.Empty;
        if (text is null)
        {
            return false;
        }

        var hex = text.Replace(":", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).Trim();
        if (hex.Length != 64 || hex.AsSpan().ContainsAnyExcept(HexDigits))
        {
            return false;
        }

        fingerprint = hex.ToLowerInvariant();
        return true;
    }

    /// <summary>The pins a cluster's metadata carries, or null when the upstream doesn't pin.</summary>
    public static UpstreamCertificatePins? FromMetadata(IReadOnlyDictionary<string, string>? metadata) =>
        metadata is not null && metadata.TryGetValue(MetadataKey, out var joined) && joined.Length > 0
            ? new UpstreamCertificatePins([.. joined.Split(',')])
            : null;

    /// <summary>Whether two clusters' metadata pin the same certificates, so a client built for one serves the other.</summary>
    public static bool SamePins(IReadOnlyDictionary<string, string>? left, IReadOnlyDictionary<string, string>? right) =>
        string.Equals(Joined(left), Joined(right), StringComparison.Ordinal);

    // The leaf alone is compared: a pinned certificate is trusted for what it is, so its name, chain and dates don't matter.
    public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        certificate is not null && _pins.Contains(Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256)));

    private static string? Joined(IReadOnlyDictionary<string, string>? metadata) =>
        metadata is not null && metadata.TryGetValue(MetadataKey, out var joined) ? joined : null;
}
