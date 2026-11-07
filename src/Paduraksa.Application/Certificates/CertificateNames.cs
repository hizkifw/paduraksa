using System.Net;

using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Application.Certificates;

/// <summary>
/// Normalises the names a certificate covers (its SANs) like host names (PLAN §5.1), so "which certificates cover this
/// name" compares equal strings: lowercase, punycode, no trailing dot, wildcards as <c>*.parent</c>, IP addresses in
/// canonical text (IPv6 without brackets).
/// </summary>
internal static class CertificateNames
{
    /// <summary>
    /// Normalises <paramref name="names"/>, dropping duplicates. A name that is neither a valid DNS name (or wildcard) nor
    /// an IP address goes to <paramref name="rejected"/>: real-world certificates carry such names (an underscore in the
    /// wrong place, an email SAN), and they can never match a host name anyway.
    /// </summary>
    public static List<string> Normalize(IEnumerable<string> names, out List<string> rejected)
    {
        List<string> normalized = [];
        rejected = [];
        foreach (string name in names)
        {
            string? value = Normalize(name);
            if (value is null)
            {
                rejected.Add(name);
            }
            else if (!normalized.Contains(value))
            {
                normalized.Add(value);
            }
        }

        return normalized;
    }

    /// <summary>The normal form of a single name; null when it isn't one.</summary>
    public static string? Normalize(string? name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length > 2 && trimmed[0] == '[' && trimmed[^1] == ']')
        {
            trimmed = trimmed[1..^1];
        }

        if (LooksLikeIp(trimmed))
        {
            // An IPv4 address is exactly four dotted decimal parts ("1.2" and "16909060" are numbers, not addresses).
            bool shapeOk = trimmed.Contains(':', StringComparison.Ordinal) || trimmed.Count(c => c == '.') == 3;
            return shapeOk && IPAddress.TryParse(trimmed, out IPAddress? address) ? address.ToString() : null;
        }

        return HostNames.TryNormalize(trimmed, out string normalized, out HostNameKind kind, out _) && kind != HostNameKind.IPv6Literal
            ? normalized
            : null;
    }

    private static bool LooksLikeIp(string name) =>
        name.Contains(':', StringComparison.Ordinal) || (name.Length > 0 && name.All(c => char.IsAsciiDigit(c) || c == '.'));

    /// <summary>The wildcard name that would cover <paramref name="name"/> (<c>*.example.com</c> for <c>www.example.com</c>), or null.</summary>
    public static string? WildcardOf(string name)
    {
        if (LooksLikeIp(name))
        {
            return null; // an address has no wildcard
        }

        int firstDot = name.IndexOf('.', StringComparison.Ordinal);
        return firstDot > 0 && !name.StartsWith("*.", StringComparison.Ordinal) ? string.Concat("*", name.AsSpan(firstDot)) : null;
    }
}
