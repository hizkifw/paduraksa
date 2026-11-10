using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Paduraksa.Proxy.Net;

namespace Paduraksa.Proxy.Snapshot;

/// <summary>The shape of a configured host name.</summary>
public enum HostNameKind
{
    /// <summary>A DNS name (or a canonical dotted-quad IPv4 literal), matched exactly.</summary>
    Exact,

    /// <summary><c>*.parent</c>: matches exactly one extra leftmost label, like a TLS wildcard.</summary>
    Wildcard,

    /// <summary>A bracketed IPv6 literal, matched by address so every spelling of it agrees.</summary>
    IPv6Literal,
}

/// <summary>Compile-time host-name normalisation: trimmed, lowercased, IDN → punycode, no trailing dot, then the DNS
/// rules shared with the certificate table (<see cref="DnsNames"/>: lengths, label characters, wildcard shape,
/// punycode that decodes). The runtime never converts anything: browsers send punycode, and a raw Unicode <c>Host</c>
/// header is a miss. Public so the config document's normalisation and validation (Application) apply the very same
/// rules.</summary>
/// <remarks>
/// What this adds to <see cref="DnsNames"/>, and the certificate table's <c>ServerNames</c> doesn't: surrounding
/// whitespace is trimmed, Unicode names are converted to punycode, and IP literals are hosts too (a canonical dotted
/// quad, or a bracketed IPv6 literal). The compiler leaves IP literals out of the certificate table, so every other
/// name accepted here is also a valid server name.
/// </remarks>
public static class HostNames
{
    private static readonly IdnMapping Idn = new();

    /// <summary>Normalises <paramref name="name"/>; on failure <paramref name="error"/> says why.</summary>
    public static bool TryNormalize(string? name, out string normalized, out HostNameKind kind, out string? error)
    {
        normalized = string.Empty;
        kind = HostNameKind.Exact;
        error = null;

        var value = (name ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            error = "a host name can't be empty";
            return false;
        }

        if (value[0] == '[')
        {
            return TryNormalizeIPv6Literal(value, out normalized, out kind, out error);
        }

        if (!Ascii.IsValid(value) && (!IsConvertibleUnicode(name, value, out error) || !TryConvertToPunycode(name, ref value, out error)))
        {
            return false;
        }

        if (!DnsNames.TryParse(value, out var parsed, out var problem))
        {
            error = $"'{name}': {problem}";
            return false;
        }

        if (parsed.IsNumeric && !IsCanonicalIPv4(parsed.Canonical))
        {
            error = $"'{name}': an IPv4 literal must be a canonical dotted quad (e.g. 192.0.2.1)";
            return false;
        }

        kind = parsed.IsWildcard ? HostNameKind.Wildcard : HostNameKind.Exact;
        normalized = parsed.IsWildcard ? "*." + parsed.Canonical : parsed.Canonical;
        return true;
    }

    /// <summary>Parses the inside of a bracketed IPv6 literal (no zone ID) into its 128-bit key with the strict, 0 B
    /// <see cref="IpParser"/>.</summary>
    internal static bool TryParseIPv6Literal(ReadOnlySpan<char> inner, out UInt128 key)
    {
        var parsed = IpParser.TryParseIPv6(inner, out var address);
        key = parsed ? address.Value : UInt128.Zero;
        return parsed;
    }

    // With invariant globalization IdnMapping does no UTS 46 mapping or NFC normalisation, so input that needs them
    // would turn into punycode no browser sends (or that differs from the composed spelling): combining marks (NFD),
    // invisible format characters (ZWJ, soft hyphen) and the halfwidth/fullwidth forms. Case is folded by the caller.
    private static bool IsConvertibleUnicode(string? name, string value, out string? error)
    {
        foreach (var c in value)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
                or UnicodeCategory.Format
                || c is >= '\uFF00' and <= '\uFFEF')
            {
                error = $"'{name}' contains U+{(int)c:X4} (a combining mark, an invisible format character or a "
                    + "fullwidth/halfwidth form), which can't be normalised here; write the name in its composed form or as "
                    + "punycode ('xn--...')";
                return false;
            }
        }

        error = null;
        return true;
    }

    // Unicode → punycode, keeping a leading "*." label and dropping a trailing dot (IdnMapping rejects both).
    // Lowercased first: with invariant globalization IdnMapping doesn't case-fold non-ASCII letters.
    private static bool TryConvertToPunycode(string? name, ref string value, out string? error)
    {
        error = null;
        value = value.ToLowerInvariant();
        var dnsPart = value.EndsWith('.') ? value[..^1] : value;
        var wildcard = dnsPart.StartsWith("*.", StringComparison.Ordinal);
        if (wildcard)
        {
            dnsPart = dnsPart[2..];
        }

        if (dnsPart.Contains('*', StringComparison.Ordinal))
        {
            error = $"'{name}': a wildcard ('*') is only allowed as the whole leftmost label ('*.example.com')";
            return false;
        }

        try
        {
            dnsPart = Idn.GetAscii(dnsPart);
        }
        catch (ArgumentException ex)
        {
            error = $"'{name}' isn't a valid internationalised domain name: {ex.Message}";
            return false;
        }

        value = wildcard ? "*." + dnsPart : dnsPart;
        return true;
    }

    private static bool TryNormalizeIPv6Literal(string value, out string normalized, out HostNameKind kind, out string? error)
    {
        normalized = string.Empty;
        kind = HostNameKind.IPv6Literal;
        error = null;
        if (value.Length < 3 || value[^1] != ']'
            || !IPAddress.TryParse(value.AsSpan(1, value.Length - 2), out var address)
            || address.AddressFamily != AddressFamily.InterNetworkV6
            || value.Contains('%', StringComparison.Ordinal))
        {
            error = $"'{value}' isn't a valid bracketed IPv6 literal (zone IDs aren't allowed)";
            return false;
        }

        normalized = "[" + address.ToString() + "]";
        return true;
    }

    private static bool IsCanonicalIPv4(string name)
    {
        var parts = 0;
        foreach (var range in name.AsSpan().Split('.'))
        {
            var part = name.AsSpan()[range];
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
