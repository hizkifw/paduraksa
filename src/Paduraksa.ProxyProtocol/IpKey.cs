using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Paduraksa.ProxyProtocol;

/// <summary>
/// An IP address as a 128-bit value: one representation for both families that compares, hashes, masks and formats without allocating.
/// </summary>
/// <remarks>
/// <para>IPv4 is stored IPv4-mapped (<c>::ffff:a.b.c.d</c>), so a plain IPv4 address and its mapped IPv6 form are
/// the same key. That matters on a dual-mode <c>[::]</c> listener, which reports an IPv4 client as
/// <c>::ffff:a.b.c.d</c>.</para>
/// <para>The deprecated IPv4-compatible form <c>::a.b.c.d</c> is <em>not</em> folded into the IPv4 key: it is a
/// different IPv6 address (and what the BCL and sockets say too). See <see cref="IpParser"/>.</para>
/// <para>IPv6 scope IDs (zones) are not part of the key.</para>
/// <para>The default value is <c>::</c>, the unspecified address (<see cref="Unspecified"/>).</para>
/// </remarks>
public readonly struct IpKey : IEquatable<IpKey>, IComparable<IpKey>, ISpanFormattable, IUtf8SpanFormattable
{
    /// <summary>Longest output of <see cref="TryFormat(Span{char}, out int)"/>: an uncompressible IPv6 address
    /// (IPv4 keys print as at most 15 characters).</summary>
    public const int MaxFormattedLength = 39;

    private const ulong MappedPrefix = 0x0000_FFFF_0000_0000UL;
    private const ulong MappedPrefixMask = 0xFFFF_FFFF_0000_0000UL;

    private readonly UInt128 _value;

    /// <summary>Wraps a raw 128-bit value (network byte order read as a big-endian integer).</summary>
    public IpKey(UInt128 value) => _value = value;

    /// <summary>The unspecified address <c>::</c> (also the default value).</summary>
    public static IpKey Unspecified => default;

    /// <summary>The 128-bit value (IPv4 as <c>::ffff:a.b.c.d</c>).</summary>
    public UInt128 Value => _value;

    /// <summary>True for an IPv4 (IPv4-mapped) key.</summary>
    public bool IsIPv4 => (ulong)(_value >> 64) == 0 && ((ulong)_value & MappedPrefixMask) == MappedPrefix;

    /// <summary>The IPv4 address as a big-endian-read <see cref="uint"/> (<c>a.b.c.d</c> = <c>a &lt;&lt; 24 | ...</c>).
    /// Only meaningful when <see cref="IsIPv4"/> is true.</summary>
    public uint IPv4Value => (uint)_value;

    /// <summary>An IPv4 key from a host-order value (<c>a.b.c.d</c> = <c>a &lt;&lt; 24 | b &lt;&lt; 16 | ...</c>).</summary>
    public static IpKey FromIPv4(uint address) => new(new UInt128(0, MappedPrefix | address));

    /// <summary>A key from 4 (IPv4) or 16 (IPv6) bytes in network order.</summary>
    public static IpKey FromBytes(ReadOnlySpan<byte> bytes) => bytes.Length switch
    {
        4 => FromIPv4(BinaryPrimitives.ReadUInt32BigEndian(bytes)),
        16 => new IpKey(BinaryPrimitives.ReadUInt128BigEndian(bytes)),
        _ => throw new ArgumentException("An IP address is 4 or 16 bytes.", nameof(bytes)),
    };

    /// <summary>The key for a socket address. Allocation-free (<see cref="IPAddress.TryWriteBytes"/> into a stack
    /// buffer); the scope ID of an IPv6 address is dropped.</summary>
    public static IpKey FromIPAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        Span<byte> bytes = stackalloc byte[16];
        // 16 bytes always fit both families, so this can't fail.
        _ = address.TryWriteBytes(bytes, out var written);
        return written == 4
            ? FromIPv4(BinaryPrimitives.ReadUInt32BigEndian(bytes))
            : new IpKey(BinaryPrimitives.ReadUInt128BigEndian(bytes));
    }

    /// <summary>The key and port of an <see cref="IPEndPoint"/>; false (and <see cref="Unspecified"/>, port 0)
    /// for anything else (null, Unix sockets, named pipes).</summary>
    public static bool TryFromEndPoint(EndPoint? endPoint, out IpKey address, out ushort port)
    {
        if (endPoint is IPEndPoint ip)
        {
            address = FromIPAddress(ip.Address);
            port = (ushort)ip.Port;
            return true;
        }

        address = default;
        port = 0;
        return false;
    }

    /// <summary>Parses with <see cref="IpParser"/> (strict; see there).</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out IpKey address) => IpParser.TryParse(text, out address);

    /// <summary>Parses UTF-8 with <see cref="IpParser"/> (strict; see there).</summary>
    public static bool TryParse(ReadOnlySpan<byte> utf8Text, out IpKey address) => IpParser.TryParse(utf8Text, out address);

    /// <summary>Parses with <see cref="IpParser"/>, throwing <see cref="FormatException"/> on invalid input.</summary>
    public static IpKey Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return IpParser.TryParse(text, out var address)
            ? address
            : throw new FormatException($"'{text}' is not a valid IP address.");
    }

    /// <summary>Writes the 16-byte network-order form (IPv4 as <c>::ffff:a.b.c.d</c>).</summary>
    public void WriteBytes(Span<byte> destination) => BinaryPrimitives.WriteUInt128BigEndian(destination, _value);

    /// <summary>An <see cref="IPAddress"/> (allocates). IPv4 keys become 4-byte addresses.</summary>
    public IPAddress ToIPAddress()
    {
        if (IsIPv4)
        {
            Span<byte> v4 = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(v4, IPv4Value);
            return new IPAddress(v4);
        }

        Span<byte> v6 = stackalloc byte[16];
        WriteBytes(v6);
        return new IPAddress(v6);
    }

    /// <summary>Keeps the first <paramref name="prefixLength"/> bits <em>of the address's own family</em>
    /// (0-32 for IPv4, 0-128 for IPv6) and zeroes the rest. An IPv4 key stays an IPv4 key.</summary>
    /// <example><c>2001:db8:1:2:3:4:5:6</c> /64 → <c>2001:db8:1:2::</c>; <c>192.0.2.77</c> /24 → <c>192.0.2.0</c>.</example>
    public IpKey MaskTo(int prefixLength)
    {
        var ipv4 = IsIPv4;
        ArgumentOutOfRangeException.ThrowIfNegative(prefixLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(prefixLength, ipv4 ? 32 : 128);
        return new IpKey(_value & PrefixMask(ipv4 ? 96 + prefixLength : prefixLength));
    }

    /// <summary><see cref="MaskTo(int)"/> with a prefix per family, e.g. <c>MaskTo(32, 64)</c> to group clients
    /// by whole IPv4 address and by IPv6 /64.</summary>
    public IpKey MaskTo(int ipv4PrefixLength, int ipv6PrefixLength) =>
        MaskTo(IsIPv4 ? ipv4PrefixLength : ipv6PrefixLength);

    /// <summary>A mask with the top <paramref name="bits"/> (0-128) of 128 set.</summary>
    private static UInt128 PrefixMask(int bits) => bits == 0 ? UInt128.Zero : UInt128.MaxValue << (128 - bits);

    /// <inheritdoc/>
    public bool Equals(IpKey other) => _value == other._value;

    /// <inheritdoc/>
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is IpKey other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _value.GetHashCode();

    /// <summary>Orders by the 128-bit value.</summary>
    public int CompareTo(IpKey other) => _value.CompareTo(other._value);

    /// <summary>Whether both keys are the same address.</summary>
    public static bool operator ==(IpKey left, IpKey right) => left._value == right._value;

    /// <summary>Whether the keys are different addresses.</summary>
    public static bool operator !=(IpKey left, IpKey right) => left._value != right._value;

    /// <summary>Compares the 128-bit values.</summary>
    public static bool operator <(IpKey left, IpKey right) => left._value < right._value;

    /// <summary>Compares the 128-bit values.</summary>
    public static bool operator <=(IpKey left, IpKey right) => left._value <= right._value;

    /// <summary>Compares the 128-bit values.</summary>
    public static bool operator >(IpKey left, IpKey right) => left._value > right._value;

    /// <summary>Compares the 128-bit values.</summary>
    public static bool operator >=(IpKey left, IpKey right) => left._value >= right._value;

    /// <summary>Dotted quad for IPv4 keys, otherwise the RFC 5952 canonical IPv6 text (lowercase, longest run of
    /// two or more zero groups compressed, no mixed notation).</summary>
    public override string ToString()
    {
        Span<char> buffer = stackalloc char[MaxFormattedLength];
        _ = TryFormat(buffer, out var written);
        return new string(buffer[..written]);
    }

    /// <summary>The same text as <see cref="ToString()"/>; the format and the provider are ignored.</summary>
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <summary>Allocation-free formatting (see <see cref="ToString()"/>); needs at most
    /// <see cref="MaxFormattedLength"/> characters.</summary>
    public bool TryFormat(Span<char> destination, out int charsWritten)
    {
        Span<char> buffer = stackalloc char[MaxFormattedLength];
        var length = IsIPv4 ? FormatIPv4(buffer, IPv4Value) : FormatIPv6(buffer, _value);
        if (!buffer[..length].TryCopyTo(destination))
        {
            charsWritten = 0;
            return false;
        }

        charsWritten = length;
        return true;
    }

    /// <summary>The same as <see cref="TryFormat(Span{char}, out int)"/>; the format and the provider are ignored.</summary>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        TryFormat(destination, out charsWritten);

    /// <summary>As <see cref="TryFormat(Span{char}, out int)"/>, as ASCII/UTF-8 bytes.</summary>
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        Span<char> buffer = stackalloc char[MaxFormattedLength];
        var length = IsIPv4 ? FormatIPv4(buffer, IPv4Value) : FormatIPv6(buffer, _value);
        if (utf8Destination.Length < length)
        {
            bytesWritten = 0;
            return false;
        }

        for (var i = 0; i < length; i++)
        {
            utf8Destination[i] = (byte)buffer[i];
        }

        bytesWritten = length;
        return true;
    }

    private static int FormatIPv4(Span<char> dest, uint v)
    {
        var pos = 0;
        for (var shift = 24; shift >= 0; shift -= 8)
        {
            var octet = (int)((v >> shift) & 0xFF);
            if (octet >= 100)
            {
                dest[pos++] = (char)('0' + (octet / 100));
            }

            if (octet >= 10)
            {
                dest[pos++] = (char)('0' + (octet / 10 % 10));
            }

            dest[pos++] = (char)('0' + (octet % 10));
            if (shift != 0)
            {
                dest[pos++] = '.';
            }
        }

        return pos;
    }

    private static int FormatIPv6(Span<char> dest, UInt128 value)
    {
        Span<ushort> groups = stackalloc ushort[8];
        for (var i = 0; i < 8; i++)
        {
            groups[i] = (ushort)(value >> (112 - (16 * i)));
        }

        // RFC 5952 §4.2: compress the longest run of two or more zero groups; the first one on a tie.
        int bestStart = -1, bestLength = 1;
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
                bestStart = start;
                bestLength = i - start;
            }
        }

        var pos = 0;
        for (var i = 0; i < 8; i++)
        {
            if (i == bestStart)
            {
                dest[pos++] = ':';
                dest[pos++] = ':';
                i += bestLength - 1;
                continue;
            }

            if (pos > 0 && dest[pos - 1] != ':')
            {
                dest[pos++] = ':';
            }

            pos += FormatHexGroup(dest[pos..], groups[i]);
        }

        return pos;
    }

    private static int FormatHexGroup(Span<char> dest, ushort group)
    {
        var pos = 0;
        var started = false;
        for (var shift = 12; shift >= 0; shift -= 4)
        {
            var nibble = (group >> shift) & 0xF;
            if (nibble == 0 && !started && shift != 0)
            {
                continue;
            }

            started = true;
            dest[pos++] = (char)(nibble < 10 ? '0' + nibble : 'a' + nibble - 10);
        }

        return pos;
    }
}
