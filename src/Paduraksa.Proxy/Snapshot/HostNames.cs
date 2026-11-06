using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Paduraksa.Proxy.Snapshot;

/// <summary>The shape of a configured host name.</summary>
internal enum HostNameKind
{
    /// <summary>A DNS name (or a canonical dotted-quad IPv4 literal), matched exactly.</summary>
    Exact,

    /// <summary><c>*.parent</c>: matches exactly one extra leftmost label, like a TLS wildcard.</summary>
    Wildcard,

    /// <summary>A bracketed IPv6 literal, matched by address so every spelling of it agrees.</summary>
    IPv6Literal,
}

/// <summary>
/// Compile-time host-name normalisation (PLAN §5.1 <c>HostName</c>): trimmed, lowercased, IDN → punycode, no trailing
/// dot, ≤ 253 chars, labels 1–63 chars, wildcard only as the leftmost <c>*.</c> label. IP literals are canonicalised.
/// The runtime never converts anything: browsers send punycode, and a raw Unicode <c>Host</c> header is a miss.
/// </summary>
internal static class HostNames
{
    private static readonly IdnMapping Idn = new();

    /// <summary>Characters allowed in a label (after lowercasing). <c>_</c> is accepted for internal names.</summary>
    internal static readonly SearchValues<char> LabelChars =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_");

    /// <summary>Normalises <paramref name="name"/>; on failure <paramref name="error"/> says why.</summary>
    public static bool TryNormalize(string? name, out string normalized, out HostNameKind kind, out string? error)
    {
        normalized = string.Empty;
        kind = HostNameKind.Exact;
        error = null;

        string value = (name ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            error = "a host name can't be empty";
            return false;
        }

        if (value[0] == '[')
        {
            return TryNormalizeIPv6Literal(value, out normalized, out kind, out error);
        }

        if (value.EndsWith('.'))
        {
            value = value[..^1];
        }

        bool wildcard = value.StartsWith("*.", StringComparison.Ordinal);
        string dnsPart = wildcard ? value[2..] : value;
        if (dnsPart.Contains('*', StringComparison.Ordinal))
        {
            error = $"'{name}': a wildcard is only allowed as the whole leftmost label ('*.example.com')";
            return false;
        }

        if (!Ascii.IsValid(dnsPart))
        {
            try
            {
                dnsPart = Idn.GetAscii(dnsPart);
            }
            catch (ArgumentException ex)
            {
                error = $"'{name}' isn't a valid internationalised domain name: {ex.Message}";
                return false;
            }
        }

        dnsPart = dnsPart.ToLowerInvariant();
        if (!IsValidDnsName(dnsPart, out string? reason))
        {
            error = $"'{name}': {reason}";
            return false;
        }

        if (LooksNumeric(dnsPart))
        {
            if (wildcard)
            {
                error = $"'{name}': a wildcard can't cover an IP address";
                return false;
            }

            if (!IsCanonicalIPv4(dnsPart))
            {
                error = $"'{name}': an IPv4 literal must be a canonical dotted quad (e.g. 192.0.2.1)";
                return false;
            }
        }

        kind = wildcard ? HostNameKind.Wildcard : HostNameKind.Exact;
        normalized = wildcard ? "*." + dnsPart : dnsPart;
        return true;
    }

    /// <summary>Parses the inside of a bracketed IPv6 literal (no zone ID) into the gate's 128-bit key.</summary>
    /// <remarks>
    /// Uses <see cref="IPAddress.TryParse(ReadOnlySpan{char}, out IPAddress?)"/>, which allocates (80 B). Only
    /// requests whose <c>Host</c> is an IPv6 literal pay it. TODO(M1 integration): switch to the zero-allocation
    /// parser from <c>Paduraksa.Proxy.Net</c>.
    /// </remarks>
    public static bool TryParseIPv6Literal(ReadOnlySpan<char> inner, out UInt128 key)
    {
        key = UInt128.Zero;
        if (inner.IsEmpty || inner.Contains('%') || !IPAddress.TryParse(inner, out IPAddress? address)
            || address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        key = Gate.ClientAddressKey.From(address);
        return true;
    }

    private static bool TryNormalizeIPv6Literal(string value, out string normalized, out HostNameKind kind, out string? error)
    {
        normalized = string.Empty;
        kind = HostNameKind.IPv6Literal;
        error = null;
        if (value.Length < 3 || value[^1] != ']'
            || !IPAddress.TryParse(value.AsSpan(1, value.Length - 2), out IPAddress? address)
            || address.AddressFamily != AddressFamily.InterNetworkV6
            || value.Contains('%', StringComparison.Ordinal))
        {
            error = $"'{value}' isn't a valid bracketed IPv6 literal (zone IDs aren't allowed)";
            return false;
        }

        normalized = "[" + address.ToString() + "]";
        return true;
    }

    private static bool IsValidDnsName(string name, out string? reason)
    {
        reason = null;
        if (name.Length is 0 or > 253)
        {
            reason = "a host name must be 1 to 253 characters long";
            return false;
        }

        foreach (Range range in name.AsSpan().Split('.'))
        {
            ReadOnlySpan<char> label = name.AsSpan()[range];
            if (label.Length is 0 or > 63)
            {
                reason = "every label must be 1 to 63 characters long";
                return false;
            }

            if (label.ContainsAnyExcept(LabelChars))
            {
                reason = "only letters, digits, '-' and '_' are allowed in a label";
                return false;
            }

            if (label[0] == '-' || label[^1] == '-')
            {
                reason = "a label can't start or end with '-'";
                return false;
            }
        }

        return true;
    }

    private static bool LooksNumeric(string name)
    {
        // The last label decides: a DNS top-level label is never all digits.
        int dot = name.LastIndexOf('.');
        ReadOnlySpan<char> last = dot < 0 ? name : name.AsSpan(dot + 1);
        return !last.ContainsAnyExceptInRange('0', '9');
    }

    private static bool IsCanonicalIPv4(string name)
    {
        int parts = 0;
        foreach (Range range in name.AsSpan().Split('.'))
        {
            ReadOnlySpan<char> part = name.AsSpan()[range];
            parts++;
            if (part.Length is 0 or > 3 || part.ContainsAnyExceptInRange('0', '9') || (part.Length > 1 && part[0] == '0')
                || int.Parse(part, CultureInfo.InvariantCulture) > 255)
            {
                return false;
            }
        }

        return parts == 4;
    }
}
