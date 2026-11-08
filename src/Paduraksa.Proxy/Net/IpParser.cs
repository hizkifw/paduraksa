using System.Numerics;

namespace Paduraksa.Proxy.Net;

/// <summary>
/// Allocation-free parser for a bare IPv4 or IPv6 literal, from UTF-16 or UTF-8 (raw header values).
/// </summary>
/// <remarks>
/// <para>It is deliberately <b>stricter than (or equal to) <see cref="System.Net.IPAddress.TryParse(ReadOnlySpan{char}, out System.Net.IPAddress?)"/></b>:
/// anything it accepts, the BCL accepts with the same address (enforced by a differential fuzz test). It never
/// guesses at forms that parse differently between implementations, since those are a spoofing and
/// deny-list-bypass risk.</para>
/// <para><b>IPv4</b>: exactly four decimal parts of 1-3 digits, each 0-255, separated by single dots. Rejected:
/// short forms (<c>1.2.3</c>, <c>16909060</c>), octal and hex parts (<c>010.0.0.1</c>, <c>0x7f.0.0.1</c>) and
/// <b>leading zeros</b> in any part (<c>01</c>, <c>00</c>; <c>0</c> itself is fine). The BCL reads a leading
/// zero as octal, other parsers read it as decimal, so it is ambiguous by construction.</para>
/// <para><b>IPv6</b>: eight groups of 1-4 hex digits (any case, leading zeros within a group allowed, as RFC 4291
/// says), at most one <c>::</c> standing for one or more zero groups (<c>::</c> alone is the unspecified address),
/// optionally ending in an embedded dotted-quad IPv4 (same IPv4 rules) as the last 32 bits.</para>
/// <para><b>Always rejected</b>: zone IDs (<c>fe80::1%eth0</c>), brackets (<c>[::1]</c>), ports, whitespace
/// anywhere (trim before calling), empty input, input longer than <see cref="MaxLength"/>, non-ASCII.</para>
/// <para><b>Deprecated IPv4-compatible form</b> (<c>::a.b.c.d</c>, RFC 4291 §2.5.5.1): accepted, and it does
/// <em>not</em> collapse to the IPv4 key for <c>a.b.c.d</c>. It is a distinct (never routed) IPv6 address, which
/// is exactly what the BCL returns too. Only <c>a.b.c.d</c> and <c>::ffff:a.b.c.d</c> give an IPv4
/// (<see cref="IpKey.IsIPv4"/>) key. IPv4 allow/deny rules therefore don't match <c>::a.b.c.d</c>; such a value
/// can only reach us in a header written by a trusted proxy.</para>
/// </remarks>
public static class IpParser
{
    /// <summary>Longest accepted input: <c>ffff:ffff:ffff:ffff:ffff:ffff:255.255.255.255</c>.</summary>
    public const int MaxLength = 45;

    /// <summary>Parses an IPv4 or IPv6 literal (rules in the type remarks).</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out IpKey address) => TryParseCore(text, out address);

    /// <summary>Parses an IPv4 or IPv6 literal from UTF-8/ASCII bytes (rules in the type remarks).</summary>
    public static bool TryParse(ReadOnlySpan<byte> utf8Text, out IpKey address) => TryParseCore(utf8Text, out address);

    /// <summary>Parses a strict dotted-quad IPv4 address only (no IPv6 forms).</summary>
    public static bool TryParseIPv4(ReadOnlySpan<char> text, out IpKey address) => TryParseIPv4Core(text, out address);

    /// <summary>Parses a strict dotted-quad IPv4 address from UTF-8 only (no IPv6 forms).</summary>
    public static bool TryParseIPv4(ReadOnlySpan<byte> utf8Text, out IpKey address) => TryParseIPv4Core(utf8Text, out address);

    /// <summary>Parses the textual IPv6 form only (must contain a colon; <c>::ffff:a.b.c.d</c> still yields an
    /// IPv4 key).</summary>
    public static bool TryParseIPv6(ReadOnlySpan<char> text, out IpKey address) => TryParseIPv6Core(text, out address);

    /// <summary>Parses the textual IPv6 form only, from UTF-8 (see <see cref="TryParseIPv6(ReadOnlySpan{char}, out IpKey)"/>).</summary>
    public static bool TryParseIPv6(ReadOnlySpan<byte> utf8Text, out IpKey address) => TryParseIPv6Core(utf8Text, out address);

    private static bool TryParseCore<T>(ReadOnlySpan<T> s, out IpKey address)
        where T : unmanaged, IBinaryInteger<T>
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (Code(s[i]) == ':')
            {
                return TryParseIPv6Core(s, out address);
            }
        }

        return TryParseIPv4Core(s, out address);
    }

    private static bool TryParseIPv4Core<T>(ReadOnlySpan<T> s, out IpKey address)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (s.Length is < 7 or > 15 || !TryParseDottedQuad(s, out var v4))
        {
            address = default;
            return false;
        }

        address = IpKey.FromIPv4(v4);
        return true;
    }

    private static bool TryParseIPv6Core<T>(ReadOnlySpan<T> s, out IpKey address)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (s.Length is < 2 or > MaxLength || !TryParseIPv6Groups(s, out var value))
        {
            address = default;
            return false;
        }

        address = new IpKey(value);
        return true;
    }

    private static uint Code<T>(T c)
        where T : unmanaged, IBinaryInteger<T> => uint.CreateTruncating(c);

    /// <summary>Exactly four 0-255 decimal parts, no leading zeros, nothing else.</summary>
    private static bool TryParseDottedQuad<T>(ReadOnlySpan<T> s, out uint value)
        where T : unmanaged, IBinaryInteger<T>
    {
        value = 0;
        int parts = 0, part = 0, digits = 0;
        for (var i = 0; i <= s.Length; i++)
        {
            if (i == s.Length || Code(s[i]) == '.')
            {
                if (digits == 0 || parts == 4)
                {
                    return false;
                }

                value = (value << 8) | (uint)part;
                parts++;
                part = 0;
                digits = 0;
                continue;
            }

            var d = Code(s[i]) - '0';
            if (d > 9 || (digits > 0 && part == 0))
            {
                // Not a digit, or a digit after a leading zero ("01", "00"): octal in some parsers, so reject.
                return false;
            }

            part = (part * 10) + (int)d;
            digits++;
            if (part > 255)
            {
                return false;
            }
        }

        return parts == 4;
    }

    private static bool TryParseIPv6Groups<T>(ReadOnlySpan<T> s, out UInt128 value)
        where T : unmanaged, IBinaryInteger<T>
    {
        value = 0;
        Span<ushort> groups = stackalloc ushort[8];
        int count = 0, gap = -1, i = 0;
        var n = s.Length;

        if (Code(s[0]) == ':')
        {
            if (Code(s[1]) != ':')
            {
                return false;
            }

            gap = 0;
            i = 2;
            if (n == 2)
            {
                return true; // "::"
            }
        }

        while (true)
        {
            var start = i;
            uint group = 0;
            var digits = 0;
            while (i < n)
            {
                var nibble = HexValue(Code(s[i]));
                if (nibble < 0)
                {
                    break;
                }

                if (++digits > 4)
                {
                    return false;
                }

                group = (group << 4) | (uint)nibble;
                i++;
            }

            if (i < n && Code(s[i]) == '.')
            {
                // Embedded IPv4: must be the final 32 bits and run to the end of the input.
                if (count > 6 || !TryParseDottedQuad(s[start..], out var v4))
                {
                    return false;
                }

                groups[count++] = (ushort)(v4 >> 16);
                groups[count++] = (ushort)v4;
                break;
            }

            if (digits == 0 || count == 8)
            {
                return false;
            }

            groups[count++] = (ushort)group;
            if (i == n)
            {
                break;
            }

            if (Code(s[i]) != ':')
            {
                return false;
            }

            i++;
            if (i == n)
            {
                return false; // trailing single ':'
            }

            if (Code(s[i]) == ':')
            {
                if (gap >= 0)
                {
                    return false; // second "::"
                }

                gap = count;
                i++;
                if (i == n)
                {
                    break; // trailing "::"
                }
            }
        }

        // Without "::" all eight groups must be present; with it, it must stand for at least one zero group.
        if (gap < 0 ? count != 8 : count > 7)
        {
            return false;
        }

        UInt128 result = 0;
        var zeros = 8 - count;
        for (var k = 0; k < count; k++)
        {
            if (k == gap)
            {
                result <<= 16 * zeros;
            }

            result = (result << 16) | groups[k];
        }

        if (gap == count)
        {
            result <<= 16 * zeros;
        }

        value = result;
        return true;
    }

    private static int HexValue(uint c) => c switch
    {
        >= '0' and <= '9' => (int)(c - '0'),
        >= 'a' and <= 'f' => (int)(c - 'a' + 10),
        >= 'A' and <= 'F' => (int)(c - 'A' + 10),
        _ => -1,
    };
}
