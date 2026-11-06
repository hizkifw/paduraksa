using System.Diagnostics.CodeAnalysis;

namespace Paduraksa.Proxy.Net;

/// <summary>
/// One CIDR block, e.g. <c>10.0.0.0/8</c> or <c>2001:db8::/32</c>, over <see cref="IpKey"/>'s 128-bit space.
/// </summary>
/// <remarks>
/// <para>An IPv4 block lives in the IPv4-mapped range: <c>10.0.0.0/8</c> is <c>::ffff:10.0.0.0/104</c>, so
/// <c>0.0.0.0/0</c> covers all of IPv4 and nothing else, while <c>::/0</c> covers everything, IPv4 included.</para>
/// <para>Parsing is strict: the address follows <see cref="IpParser"/>, the prefix is 0–32 for a dotted-quad
/// address and 0–128 for an IPv6-text address, written in decimal without leading zeros or signs. Host bits set
/// below the prefix (<c>10.0.0.1/8</c>) are rejected rather than silently masked: in a trust list that's usually
/// a typo for a single address. A bare address is a /32 or /128.</para>
/// </remarks>
public readonly struct IpNetwork : IEquatable<IpNetwork>
{
    private IpNetwork(IpKey first, int prefix128)
    {
        First = first;
        Prefix128 = prefix128;
    }

    /// <summary>The network address (lowest address in the block).</summary>
    public IpKey First { get; }

    /// <summary>The highest address in the block.</summary>
    public IpKey Last => new(First.Value | ~IpKey.PrefixMask(Prefix128));

    /// <summary>The prefix length in the 128-bit key space (an IPv4 /8 is 104).</summary>
    public int Prefix128 { get; }

    /// <summary>True when the block is inside the IPv4(-mapped) range (/96 or longer over <c>::ffff:0:0</c>).</summary>
    public bool IsIPv4 => First.IsIPv4 && Prefix128 >= 96;

    /// <summary>The prefix length in the block's own family (0–32 for IPv4, 0–128 for IPv6).</summary>
    public int PrefixLength => IsIPv4 ? Prefix128 - 96 : Prefix128;

    /// <summary>A block from a network address and a family-relative prefix length; host bits are masked off.</summary>
    public static IpNetwork Create(IpKey address, int prefixLength)
    {
        var masked = address.MaskTo(prefixLength);
        return new IpNetwork(masked, address.IsIPv4 ? 96 + prefixLength : prefixLength);
    }

    /// <summary>Whether <paramref name="address"/> falls in this block.</summary>
    public bool Contains(IpKey address) => (address.Value & IpKey.PrefixMask(Prefix128)) == First.Value;

    /// <summary>Parses <c>address/prefix</c> or a bare address (strict; see the type remarks).</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out IpNetwork network) => TryParse(text, out network, out _);

    /// <summary>As <see cref="TryParse(ReadOnlySpan{char}, out IpNetwork)"/>, with a reason on failure (for config
    /// validation messages).</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out IpNetwork network, [NotNullWhen(false)] out string? error)
    {
        network = default;
        var slash = text.IndexOf('/');
        var addressText = slash < 0 ? text : text[..slash];
        if (!IpParser.TryParse(addressText, out var address))
        {
            error = "invalid IP address";
            return false;
        }

        var isIPv4Text = addressText.IndexOf(':') < 0;
        var maxPrefix = isIPv4Text ? 32 : 128;
        var prefix = maxPrefix;
        if (slash >= 0 && !TryParsePrefix(text[(slash + 1)..], maxPrefix, out prefix))
        {
            error = isIPv4Text
                ? "invalid prefix length (an IPv4 prefix is 0-32, without leading zeros)"
                : "invalid prefix length (an IPv6 prefix is 0-128, without leading zeros)";
            return false;
        }

        var prefix128 = isIPv4Text ? 96 + prefix : prefix;
        if ((address.Value & ~IpKey.PrefixMask(prefix128)) != 0)
        {
            error = "host bits are set below the prefix length (did you mean the network address?)";
            return false;
        }

        network = new IpNetwork(address, prefix128);
        error = null;
        return true;
    }

    /// <summary>Parses, throwing <see cref="FormatException"/> with the reason.</summary>
    public static IpNetwork Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(text, out var network, out var error)
            ? network
            : throw new FormatException($"'{text}' is not a valid CIDR block: {error}.");
    }

    private static bool TryParsePrefix(ReadOnlySpan<char> s, int max, out int prefix)
    {
        prefix = 0;
        if (s.Length is 0 or > 3 || (s.Length > 1 && s[0] == '0'))
        {
            return false;
        }

        foreach (var c in s)
        {
            var d = c - '0';
            if ((uint)d > 9)
            {
                return false;
            }

            prefix = (prefix * 10) + d;
        }

        return prefix <= max;
    }

    public bool Equals(IpNetwork other) => First == other.First && Prefix128 == other.Prefix128;

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is IpNetwork other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(First, Prefix128);

    public static bool operator ==(IpNetwork left, IpNetwork right) => left.Equals(right);

    public static bool operator !=(IpNetwork left, IpNetwork right) => !left.Equals(right);

    /// <summary><c>10.0.0.0/8</c>, <c>2001:db8::/32</c>; an IPv4-mapped block shorter than /96 prints in IPv6 form.</summary>
    public override string ToString() => $"{First}/{PrefixLength}";
}
