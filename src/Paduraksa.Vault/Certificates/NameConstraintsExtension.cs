using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

using Paduraksa.Core.Vault;

namespace Paduraksa.Vault.Certificates;

/// <summary>
/// The X.509 name-constraints extension (RFC 5280 4.2.1.10, OID 2.5.29.30) of a local CA root: permitted DNS subtrees and permitted
/// IP networks, nothing excluded. .NET has no type for it, so it is encoded and read here. The extension is critical, as RFC 5280
/// requires of a CA that uses it: a client that doesn't understand it must refuse the root rather than ignore the limit.
/// </summary>
internal static class NameConstraintsExtension
{
    public const string Oid = "2.5.29.30";

    private static readonly Asn1Tag Permitted = new(TagClass.ContextSpecific, 0, isConstructed: true);
    private static readonly Asn1Tag DnsName = new(TagClass.ContextSpecific, 2);
    private static readonly Asn1Tag IpAddress = new(TagClass.ContextSpecific, 7);

    /// <summary>The checked, normalised form: domains lowercase without dots or wildcards, networks in canonical CIDR text.</summary>
    /// <exception cref="ArgumentException">An entry isn't a DNS domain or a CIDR network, or a list is empty or too long.</exception>
    public static LocalCaConstraints Normalize(LocalCaConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        if (constraints.Domains.Count == 0 && constraints.Addresses.Count == 0)
        {
            throw new ArgumentException("A local CA needs at least one permitted domain or network.", nameof(constraints));
        }

        if (constraints.Domains.Count > LocalCaConstraints.MaxEntries || constraints.Addresses.Count > LocalCaConstraints.MaxEntries)
        {
            throw new ArgumentException($"A local CA's constraints list at most {LocalCaConstraints.MaxEntries} domains and {LocalCaConstraints.MaxEntries} networks.", nameof(constraints));
        }

        List<string> domains = [];
        foreach (var domain in constraints.Domains)
        {
            var trimmed = (domain ?? string.Empty).Trim().TrimStart('.');
            if (trimmed.StartsWith("*.", StringComparison.Ordinal))
            {
                trimmed = trimmed[2..];
            }

            if (CertificateNaming.NormalizeOne(trimmed) is not { } normal || IPAddress.TryParse(normal, out _) || normal.Contains('*', StringComparison.Ordinal))
            {
                throw new ArgumentException($"'{domain}' is not a DNS domain.", nameof(constraints));
            }

            if (!domains.Contains(normal, StringComparer.Ordinal))
            {
                domains.Add(normal);
            }
        }

        List<string> networks = [];
        foreach (var address in constraints.Addresses)
        {
            var network = ParseNetwork(address) ?? throw new ArgumentException($"'{address}' is not an IP address or a CIDR network.", nameof(constraints));
            var text = network.ToString();
            if (!networks.Contains(text, StringComparer.Ordinal))
            {
                networks.Add(text);
            }
        }

        return new LocalCaConstraints(domains, networks);
    }

    public static X509Extension Build(LocalCaConstraints normalized)
    {
        AsnWriter writer = new(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence(Permitted))
        {
            foreach (var domain in normalized.Domains)
            {
                using (writer.PushSequence())
                {
                    writer.WriteCharacterString(UniversalTagNumber.IA5String, domain, DnsName);
                }
            }

            foreach (var text in normalized.Addresses)
            {
                var network = ParseNetwork(text)!.Value;
                var address = network.BaseAddress.GetAddressBytes();
                var mask = Mask(address.Length, network.PrefixLength);
                using (writer.PushSequence())
                {
                    writer.WriteOctetString([.. address, .. mask], IpAddress);
                }
            }
        }

        return new X509Extension(new System.Security.Cryptography.Oid(Oid), writer.Encode(), critical: true);
    }

    /// <summary>The permitted subtrees of <paramref name="certificate"/>, or null when it carries no name constraints.</summary>
    public static LocalCaConstraints? Read(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        foreach (var extension in certificate.Extensions)
        {
            if (extension.Oid?.Value != Oid)
            {
                continue;
            }

            List<string> domains = [];
            List<string> addresses = [];
            var sequence = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
            while (sequence.HasData)
            {
                var tag = sequence.PeekTag();
                if (tag != Permitted)
                {
                    sequence.ReadEncodedValue();
                    continue;
                }

                var subtrees = sequence.ReadSequence(Permitted);
                while (subtrees.HasData)
                {
                    var subtree = subtrees.ReadSequence();
                    var nameTag = subtree.PeekTag();
                    if (nameTag == DnsName)
                    {
                        domains.Add(subtree.ReadCharacterString(UniversalTagNumber.IA5String, DnsName).ToLowerInvariant());
                    }
                    else if (nameTag == IpAddress)
                    {
                        var bytes = subtree.ReadOctetString(IpAddress);
                        if (bytes.Length is 8 or 32)
                        {
                            var half = bytes.Length / 2;
                            var prefix = PrefixOf(bytes.AsSpan(half));
                            addresses.Add($"{new IPAddress(bytes.AsSpan(0, half))}/{prefix}");
                        }
                    }
                }
            }

            return new LocalCaConstraints(domains, addresses);
        }

        return null;
    }

    /// <summary>The entries as <c>DNS:domain</c> and <c>IP:network</c> text, for display and for <see cref="CertificateInfo.NameConstraints"/>.</summary>
    public static IReadOnlyList<string> Describe(LocalCaConstraints constraints) =>
        [.. constraints.Domains.Select(d => "DNS:" + d), .. constraints.Addresses.Select(a => "IP:" + a)];

    /// <summary>Whether <paramref name="name"/> (a DNS name, a <c>*.</c> wildcard or an IP address, as <see cref="CertificateNaming.Normalize"/> leaves it) is inside the constraints.</summary>
    public static bool Permits(LocalCaConstraints constraints, string name)
    {
        if (IPAddress.TryParse(name, out var address))
        {
            foreach (var text in constraints.Addresses)
            {
                if (ParseNetwork(text) is { } network && network.Contains(address))
                {
                    return true;
                }
            }

            return false;
        }

        // A wildcard certificate is only as wide as its parent: *.nas.lan is permitted when nas.lan is.
        var host = name.StartsWith("*.", StringComparison.Ordinal) ? name[2..] : name;
        foreach (var domain in constraints.Domains)
        {
            if (host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IPNetwork? ParseNetwork(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();
        if (!value.Contains('/', StringComparison.Ordinal))
        {
            if (!IPAddress.TryParse(value, out var single) || (single.AddressFamily == AddressFamily.InterNetworkV6 && single.ScopeId != 0))
            {
                return null;
            }

            value = $"{single}/{(single.AddressFamily == AddressFamily.InterNetwork ? 32 : 128)}";
        }

        return IPNetwork.TryParse(value, out var network) ? network : null;
    }

    private static byte[] Mask(int length, int prefix)
    {
        var mask = new byte[length];
        for (var i = 0; i < length; i++)
        {
            var bits = Math.Clamp(prefix - (i * 8), 0, 8);
            mask[i] = (byte)(0xFF << (8 - bits));
        }

        return mask;
    }

    private static int PrefixOf(ReadOnlySpan<byte> mask)
    {
        var bits = 0;
        foreach (var b in mask)
        {
            bits += System.Numerics.BitOperations.PopCount(b);
        }

        return bits;
    }
}
