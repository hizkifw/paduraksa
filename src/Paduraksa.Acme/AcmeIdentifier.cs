using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;

namespace Paduraksa.Acme;

/// <summary>An identifier an order is for (RFC 8555 §9.7.7): a DNS name, wildcards included, or an IP address (RFC 8738).</summary>
public sealed record AcmeIdentifier(string Type, string Value)
{
    public const string TypeDns = "dns";
    public const string TypeIp = "ip";

    /// <summary><see langword="true"/> for a <c>dns</c> identifier of the form <c>*.example.com</c>.</summary>
    [JsonIgnore]
    public bool IsWildcard => Type == TypeDns && Value.StartsWith("*.", StringComparison.Ordinal);

    /// <summary>
    /// A DNS identifier: an ASCII letters-digits-hyphen name, optionally with a leading <c>*.</c>. It is lowercased and one
    /// trailing dot is dropped. Converting an internationalized name to its A-label (punycode) form is the caller's job.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not an ASCII LDH name.</exception>
    public static AcmeIdentifier Dns(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var value = name.Trim();
        if (value.EndsWith('.'))
        {
            value = value[..^1];
        }

        var host = value.StartsWith("*.", StringComparison.Ordinal) ? value[2..] : value;
        if (!IsLdhName(host))
        {
            throw new ArgumentException(
                $"'{name}' is not an ASCII letters-digits-hyphen DNS name (convert internationalized names to A-labels first).", nameof(name));
        }

        return new AcmeIdentifier(TypeDns, value.ToLowerInvariant());
    }

    /// <summary>An IP identifier (RFC 8738), written in the RFC 5952 canonical text form.</summary>
    /// <exception cref="ArgumentException">The address has a scope ID, which means nothing to a CA.</exception>
    public static AcmeIdentifier Ip(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
        {
            throw new ArgumentException("An IP identifier cannot carry a scope ID.", nameof(address));
        }

        return new AcmeIdentifier(TypeIp, address.AddressFamily == AddressFamily.InterNetwork ? address.ToString() : FormatIPv6(address));
    }

    /// <summary>Whether both members are present: a CA's JSON can omit either, and the record then holds null.</summary>
    internal bool IsComplete => Type is not null && Value is not null;

    private static bool IsLdhName(string host)
    {
        if (host.Length is 0 or > 253)
        {
            return false;
        }

        foreach (var label in host.Split('.'))
        {
            if (label.Length is 0 or > 63
                || label[0] == '-'
                || label[^1] == '-'
                || !label.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>RFC 5952: lowercase, no leading zeros, the longest run of two or more zero groups (the first on a tie) as <c>::</c>.</summary>
    private static string FormatIPv6(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        Span<ushort> groups = stackalloc ushort[8];
        for (var i = 0; i < 8; i++)
        {
            groups[i] = (ushort)((bytes[2 * i] << 8) | bytes[(2 * i) + 1]);
        }

        int bestStart = -1, bestLength = 0;
        for (var i = 0; i < 8;)
        {
            if (groups[i] != 0)
            {
                i++;
                continue;
            }

            var start = i;
            while (i < 8 && groups[i] == 0)
            {
                i++;
            }

            if (i - start > bestLength)
            {
                (bestStart, bestLength) = (start, i - start);
            }
        }

        if (bestLength < 2)
        {
            bestStart = -1;
        }

        var text = new StringBuilder();
        for (var i = 0; i < 8; i++)
        {
            if (i == bestStart)
            {
                text.Append("::");
                i += bestLength - 1;
                continue;
            }

            if (text.Length > 0 && text[^1] != ':')
            {
                text.Append(':');
            }

            text.Append(groups[i].ToString("x", System.Globalization.CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }
}
