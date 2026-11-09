using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Paduraksa.Core.Ids;

/// <summary>
/// Fixed-width base62 codec using the alphabet <c>0-9A-Za-z</c> (ASCII order). Values are left-padded with
/// <c>0</c> to <see cref="Width128"/> or <see cref="Width64"/> chars, so ordinal string order equals numeric order.
/// </summary>
/// <remarks>
/// All span-based members are allocation-free. Decoding rejects inputs of the wrong length, characters outside the
/// alphabet, and values that overflow the target type. <see cref="Guid"/> values are encoded via their
/// big-endian (RFC 9562) byte order, so UUIDv7 strings sort by creation time.
/// </remarks>
public static class Base62
{
    /// <summary>The alphabet, in ASCII (and therefore ordinal) order.</summary>
    // Not const, so consumers read it from this assembly instead of baking a copy in at compile time.
    public static readonly string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>Encoded length of a 128-bit value (62^22 &gt; 2^128).</summary>
    public const int Width128 = 22;

    /// <summary>Encoded length of a 64-bit value (62^11 &gt; 2^64).</summary>
    public const int Width64 = 11;

    // 62^10 is the largest power of 62 below 2^64; values are encoded in 10-digit chunks using 64-bit arithmetic.
    private const ulong Pow10 = 839_299_365_868_340_224UL;
    private const int ChunkDigits = 10;

    private static readonly UInt128 Pow20 = (UInt128)Pow10 * Pow10;
    private static readonly UInt128 MaxHigh128 = UInt128.MaxValue / Pow20;
    private const ulong MaxHigh64 = ulong.MaxValue / Pow10;

    // The table the codec actually uses. Must equal Alphabet (checked by Base62Tests).
    private static ReadOnlySpan<byte> AlphabetUtf8 => "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"u8;

    public static bool TryEncode(UInt128 value, Span<char> destination) => TryEncodeCore(value, destination);

    public static bool TryEncode(UInt128 value, Span<byte> utf8Destination) => TryEncodeCore(value, utf8Destination);

    public static bool TryEncode(ulong value, Span<char> destination) => TryEncodeCore(value, destination);

    public static bool TryEncode(ulong value, Span<byte> utf8Destination) => TryEncodeCore(value, utf8Destination);

    public static bool TryEncode(Guid value, Span<char> destination) => TryEncodeCore(ToUInt128(value), destination);

    public static bool TryEncode(Guid value, Span<byte> utf8Destination) => TryEncodeCore(ToUInt128(value), utf8Destination);

    public static string Encode(UInt128 value) => string.Create(Width128, value, static (span, v) => TryEncodeCore(v, span));

    public static string Encode(ulong value) => string.Create(Width64, value, static (span, v) => TryEncodeCore(v, span));

    public static string Encode(Guid value) => Encode(ToUInt128(value));

    public static bool TryDecodeUInt128(ReadOnlySpan<char> source, out UInt128 value) => TryDecodeCore(source, out value);

    public static bool TryDecodeUInt128(ReadOnlySpan<byte> utf8Source, out UInt128 value) => TryDecodeCore(utf8Source, out value);

    public static bool TryDecodeUInt64(ReadOnlySpan<char> source, out ulong value) => TryDecodeCore(source, out value);

    public static bool TryDecodeUInt64(ReadOnlySpan<byte> utf8Source, out ulong value) => TryDecodeCore(utf8Source, out value);

    /// <summary>Decodes exactly <see cref="Width128"/> chars into a <see cref="Guid"/> (big-endian byte order).</summary>
    public static bool TryDecodeGuid(ReadOnlySpan<char> source, out Guid value)
    {
        bool ok = TryDecodeCore(source, out UInt128 raw);
        value = ok ? FromUInt128(raw) : default;
        return ok;
    }

    /// <summary>Decodes exactly <see cref="Width128"/> UTF-8 bytes into a <see cref="Guid"/> (big-endian byte order).</summary>
    public static bool TryDecodeGuid(ReadOnlySpan<byte> utf8Source, out Guid value)
    {
        bool ok = TryDecodeCore(utf8Source, out UInt128 raw);
        value = ok ? FromUInt128(raw) : default;
        return ok;
    }

    /// <summary>Interprets the RFC 9562 (big-endian) byte layout of <paramref name="value"/> as an unsigned integer.</summary>
    public static UInt128 ToUInt128(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        return BinaryPrimitives.ReadUInt128BigEndian(bytes);
    }

    /// <summary>Inverse of <see cref="ToUInt128(Guid)"/>.</summary>
    public static Guid FromUInt128(UInt128 value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(bytes, value);
        return new Guid(bytes, bigEndian: true);
    }

    internal static bool TryEncodeCore<TChar>(UInt128 value, Span<TChar> destination)
        where TChar : unmanaged, IBinaryInteger<TChar>
    {
        if (destination.Length < Width128)
        {
            return false;
        }

        // 22 digits = 2 (high) + 10 (mid) + 10 (low).
        (UInt128 upper, UInt128 low) = UInt128.DivRem(value, Pow10);
        (UInt128 high, UInt128 mid) = UInt128.DivRem(upper, Pow10);
        EncodeChunk((ulong)high, destination[..2]);
        EncodeChunk((ulong)mid, destination.Slice(2, ChunkDigits));
        EncodeChunk((ulong)low, destination.Slice(2 + ChunkDigits, ChunkDigits));
        return true;
    }

    internal static bool TryEncodeCore<TChar>(ulong value, Span<TChar> destination)
        where TChar : unmanaged, IBinaryInteger<TChar>
    {
        if (destination.Length < Width64)
        {
            return false;
        }

        // 11 digits = 1 (high) + 10 (low).
        (ulong high, ulong low) = Math.DivRem(value, Pow10);
        EncodeChunk(high, destination[..1]);
        EncodeChunk(low, destination.Slice(1, ChunkDigits));
        return true;
    }

    private static void EncodeChunk<TChar>(ulong value, Span<TChar> destination)
        where TChar : unmanaged, IBinaryInteger<TChar>
    {
        ReadOnlySpan<byte> alphabet = AlphabetUtf8;
        for (int i = destination.Length - 1; i >= 0; i--)
        {
            (value, ulong digit) = Math.DivRem(value, 62UL);
            destination[i] = TChar.CreateTruncating(alphabet[(int)digit]);
        }
    }

    internal static bool TryDecodeCore<TChar>(ReadOnlySpan<TChar> source, out UInt128 value)
        where TChar : unmanaged, IBinaryInteger<TChar>
    {
        value = default;
        if (source.Length != Width128
            || !TryDecodeChunk(source[..2], out ulong high)
            || !TryDecodeChunk(source.Slice(2, ChunkDigits), out ulong mid)
            || !TryDecodeChunk(source.Slice(2 + ChunkDigits, ChunkDigits), out ulong low))
        {
            return false;
        }

        if (high > MaxHigh128)
        {
            return false;
        }

        UInt128 result = high * Pow20;
        UInt128 rest = ((UInt128)mid * Pow10) + low; // < 62^20, cannot overflow
        if (rest > UInt128.MaxValue - result)
        {
            return false;
        }

        value = result + rest;
        return true;
    }

    internal static bool TryDecodeCore<TChar>(ReadOnlySpan<TChar> source, out ulong value)
        where TChar : unmanaged, IBinaryInteger<TChar>
    {
        value = default;
        if (source.Length != Width64
            || !TryDecodeChunk(source[..1], out ulong high)
            || !TryDecodeChunk(source.Slice(1, ChunkDigits), out ulong low)
            || high > MaxHigh64)
        {
            return false;
        }

        ulong result = high * Pow10;
        if (low > ulong.MaxValue - result)
        {
            return false;
        }

        value = result + low;
        return true;
    }

    // At most 10 digits, so the result is < 62^10 and fits in a ulong.
    private static bool TryDecodeChunk<TChar>(ReadOnlySpan<TChar> source, out ulong value)
        where TChar : unmanaged, IBinaryInteger<TChar>
    {
        ulong result = 0;
        foreach (TChar c in source)
        {
            int digit = DigitOf(uint.CreateTruncating(c));
            if (digit < 0)
            {
                value = 0;
                return false;
            }

            result = (result * 62) + (uint)digit;
        }

        value = result;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DigitOf(uint c)
    {
        if (c - '0' <= 9)
        {
            return (int)(c - '0');
        }

        if (c - 'A' <= 'Z' - 'A')
        {
            return (int)(c - 'A' + 10);
        }

        if (c - 'a' <= 'z' - 'a')
        {
            return (int)(c - 'a' + 36);
        }

        return -1;
    }
}
