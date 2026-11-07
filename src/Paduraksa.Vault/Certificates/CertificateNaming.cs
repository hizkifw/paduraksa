using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Paduraksa.Vault.Certificates;

/// <summary>
/// What the vault accepts as the names of a certificate it creates, and how they become the subject and the
/// SAN extension. The caller has normalised them already (PLAN §5.1); the vault checks again because it is the one that
/// puts them into a signed certificate, and an invalid name there cannot be taken back.
/// </summary>
internal static class CertificateNaming
{
    /// <summary>The most names one certificate carries; far above what a CA allows (100) and what a homelab needs.</summary>
    public const int MaxNames = 100;

    /// <summary>The RFC 5280 limit of a common name.</summary>
    public const int MaxCommonNameLength = 64;

    /// <summary>
    /// Checks and normalises names: lowercase, IP addresses in their canonical text, duplicates dropped, order kept.
    /// A DNS name is 1 to 253 ASCII characters in labels of 1 to 63 letters, digits, <c>-</c> and <c>_</c> (no leading or trailing
    /// hyphen), and may start with one <c>*.</c> wildcard label that has at least two labels behind it.
    /// </summary>
    /// <exception cref="ArgumentException">There are none, too many, or one is not a DNS name, wildcard or IP address.</exception>
    public static IReadOnlyList<string> Normalize(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
        {
            throw new ArgumentException("A certificate needs at least one name.", nameof(names));
        }

        if (names.Count > MaxNames)
        {
            throw new ArgumentException($"A certificate has at most {MaxNames} names.", nameof(names));
        }

        List<string> result = new(names.Count);
        foreach (var name in names)
        {
            var normalized = NormalizeOne(name) ?? throw new ArgumentException($"'{name}' is not a DNS name, a wildcard or an IP address.", nameof(names));
            if (!result.Contains(normalized, StringComparer.Ordinal))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    /// <summary>
    /// The subject for <paramref name="names"/>: <c>CN=</c> and the first name if it is a DNS name of at most 64 characters,
    /// otherwise empty (the SANs carry the identity).
    /// </summary>
    public static X500DistinguishedName SubjectOf(IReadOnlyList<string> names) =>
        names[0].Length <= MaxCommonNameLength && !IPAddress.TryParse(names[0], out _)
            ? new X500DistinguishedName("CN=" + names[0])
            : new X500DistinguishedName(string.Empty);

    /// <summary>The SAN extension. RFC 5280 requires it to be critical when the subject is empty.</summary>
    public static X509Extension SanOf(IReadOnlyList<string> names, X500DistinguishedName subject)
    {
        SubjectAlternativeNameBuilder builder = new();
        foreach (var name in names)
        {
            if (IPAddress.TryParse(name, out var address))
            {
                builder.AddIpAddress(address);
            }
            else
            {
                builder.AddDnsName(name);
            }
        }

        return builder.Build(critical: subject.RawData.Length <= 2);
    }

    /// <summary>True if the certificate name <paramref name="san"/> covers <paramref name="name"/>: the same name, or the wildcard of its parent (<c>*.example.com</c> covers <c>www.example.com</c> only).</summary>
    public static bool Covers(string san, string name)
    {
        if (string.Equals(san, name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!san.StartsWith("*.", StringComparison.Ordinal) || name.StartsWith("*.", StringComparison.Ordinal))
        {
            return false;
        }

        var dot = name.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 && name.AsSpan(dot + 1).Equals(san.AsSpan(2), StringComparison.OrdinalIgnoreCase);
    }

    internal static string? NormalizeOne(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        if (IPAddress.TryParse(name, out var address))
        {
            // A scope ID does not belong in a certificate, and IPv4-mapped forms are the IPv4 address.
            if (address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return address.ToString();
            }

            return address.ScopeId != 0 ? null : (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        }

        if (name.Length > 253)
        {
            return null;
        }

        var lower = name.ToLowerInvariant();
        var labels = lower.Split('.');
        var start = 0;
        if (labels[0] == "*")
        {
            if (labels.Length < 3)
            {
                return null;
            }

            start = 1;
        }

        for (var i = start; i < labels.Length; i++)
        {
            if (!IsLabel(labels[i]))
            {
                return null;
            }
        }

        return lower;
    }

    private static bool IsLabel(string label)
    {
        if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-')
        {
            return false;
        }

        foreach (var c in label)
        {
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}
