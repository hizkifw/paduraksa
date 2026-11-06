using System.ComponentModel;
using System.Numerics;

namespace Paduraksa.Core.Ids;

/// <summary>
/// Formatting and parsing primitives shared by all generated typed IDs. Keeps the generated code thin.
/// All members are allocation-free.
/// </summary>
/// <remarks>
/// <c>prefix</c> arguments include the trailing underscore (e.g. <c>host_</c>). <see cref="long"/> IDs are never
/// negative: parsing rejects values above <see cref="long.MaxValue"/>, so string order, signed order and
/// database (SQLite <c>INTEGER</c>) order all agree.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class TypedIdCodec
{
    public static bool TryFormat(Guid value, ReadOnlySpan<char> prefix, Span<char> destination, out int charsWritten) =>
        TryFormatCore(Base62.ToUInt128(value), prefix, destination, out charsWritten);

    public static bool TryFormat(Guid value, ReadOnlySpan<byte> utf8Prefix, Span<byte> utf8Destination, out int bytesWritten) =>
        TryFormatCore(Base62.ToUInt128(value), utf8Prefix, utf8Destination, out bytesWritten);

    public static bool TryFormat(long value, ReadOnlySpan<char> prefix, Span<char> destination, out int charsWritten) =>
        TryFormatCore((ulong)value, prefix, destination, out charsWritten);

    public static bool TryFormat(long value, ReadOnlySpan<byte> utf8Prefix, Span<byte> utf8Destination, out int bytesWritten) =>
        TryFormatCore((ulong)value, utf8Prefix, utf8Destination, out bytesWritten);

    public static bool TryParse(ReadOnlySpan<char> s, ReadOnlySpan<char> prefix, out Guid value) =>
        TryParseCore(s, prefix, out value);

    public static bool TryParse(ReadOnlySpan<byte> utf8Text, ReadOnlySpan<byte> utf8Prefix, out Guid value) =>
        TryParseCore(utf8Text, utf8Prefix, out value);

    public static bool TryParse(ReadOnlySpan<char> s, ReadOnlySpan<char> prefix, out long value) =>
        TryParseCore(s, prefix, out value);

    public static bool TryParse(ReadOnlySpan<byte> utf8Text, ReadOnlySpan<byte> utf8Prefix, out long value) =>
        TryParseCore(utf8Text, utf8Prefix, out value);

    public static FormatException ParseError(string typeName, string prefix, int width) =>
        new($"Invalid {typeName}: expected '{prefix}' followed by {width} base62 characters [0-9A-Za-z].");

    private static bool TryFormatCore<TChar>(UInt128 value, ReadOnlySpan<TChar> prefix, Span<TChar> destination, out int written)
        where TChar : unmanaged, IBinaryInteger<TChar>
    {
        int length = prefix.Length + Base62.Width128;
        if (destination.Length < length)
        {
            written = 0;
            return false;
        }

        prefix.CopyTo(destination);
        Base62.TryEncodeCore(value, destination[prefix.Length..]);
        written = length;
        return true;
    }

    private static bool TryFormatCore<TChar>(ulong value, ReadOnlySpan<TChar> prefix, Span<TChar> destination, out int written)
        where TChar : unmanaged, IBinaryInteger<TChar>
    {
        int length = prefix.Length + Base62.Width64;
        if (destination.Length < length)
        {
            written = 0;
            return false;
        }

        prefix.CopyTo(destination);
        Base62.TryEncodeCore(value, destination[prefix.Length..]);
        written = length;
        return true;
    }

    private static bool TryParseCore<TChar>(ReadOnlySpan<TChar> s, ReadOnlySpan<TChar> prefix, out Guid value)
        where TChar : unmanaged, IBinaryInteger<TChar>
    {
        if (s.Length == prefix.Length + Base62.Width128 && s.StartsWith(prefix) && Base62.TryDecodeCore(s[prefix.Length..], out UInt128 raw))
        {
            value = Base62.FromUInt128(raw);
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryParseCore<TChar>(ReadOnlySpan<TChar> s, ReadOnlySpan<TChar> prefix, out long value)
        where TChar : unmanaged, IBinaryInteger<TChar>
    {
        if (s.Length == prefix.Length + Base62.Width64 && s.StartsWith(prefix) && Base62.TryDecodeCore(s[prefix.Length..], out ulong raw)
            && raw <= long.MaxValue)
        {
            value = (long)raw;
            return true;
        }

        value = default;
        return false;
    }
}
