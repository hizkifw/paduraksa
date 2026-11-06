using System.Numerics;
using System.Text;

using Paduraksa.Core.Ids;

namespace Paduraksa.Tests.Unit.Ids;

public sealed class Base62Tests
{
    private static readonly UInt128[] Edge128 =
    [
        UInt128.Zero, UInt128.One, 61, 62, 63, ulong.MaxValue, (UInt128)ulong.MaxValue + 1,
        UInt128.MaxValue - 1, UInt128.MaxValue,
    ];

    private static readonly ulong[] Edge64 = [0, 1, 61, 62, 63, long.MaxValue, (ulong)long.MaxValue + 1, ulong.MaxValue - 1, ulong.MaxValue];

    [Fact]
    public void AlphabetIsInOrdinalOrder()
    {
        string sorted = new([.. Base62.Alphabet.Order()]);
        Assert.Equal(Base62.Alphabet, sorted);
        Assert.Equal(62, Base62.Alphabet.Distinct().Count());
    }

    [Fact]
    public void CodecDigitTableMatchesAlphabet()
    {
        // The codec encodes from a private UTF-8 copy of the alphabet; encoding each digit 0..61 reads it back out.
        byte[] utf8Digits = new byte[62];
        char[] charDigits = new char[62];
        Span<byte> utf8 = stackalloc byte[Base62.Width64];
        Span<char> chars = stackalloc char[Base62.Width64];
        for (int digit = 0; digit < 62; digit++)
        {
            Assert.True(Base62.TryEncode((ulong)digit, utf8));
            Assert.True(Base62.TryEncode((ulong)digit, chars));
            utf8Digits[digit] = utf8[^1];
            charDigits[digit] = chars[^1];
        }

        Assert.Equal(Encoding.UTF8.GetBytes(Base62.Alphabet), utf8Digits);
        Assert.Equal(Base62.Alphabet, new string(charDigits));
    }

    [Fact]
    public void KnownValues()
    {
        Assert.Equal(new string('0', 22), Base62.Encode(UInt128.Zero));
        Assert.Equal(new string('0', 11), Base62.Encode(0UL));
        Assert.Equal("0000000000z", Base62.Encode(61UL));
        Assert.Equal("00000000010", Base62.Encode(62UL));
        Assert.Equal(ReferenceEncode(UInt128.MaxValue, 22), Base62.Encode(UInt128.MaxValue));
        Assert.Equal(ReferenceEncode(ulong.MaxValue, 11), Base62.Encode(ulong.MaxValue));
    }

    [Fact]
    public void RoundTrip128EdgesAndRandom()
    {
        Span<byte> utf8 = stackalloc byte[Base62.Width128];
        foreach (UInt128 value in Edge128.Concat(Random128(2000)))
        {
            string encoded = Base62.Encode(value);
            Assert.Equal(Base62.Width128, encoded.Length);
            Assert.Equal(ReferenceEncode(value, 22), encoded);
            Assert.True(Base62.TryDecodeUInt128(encoded, out UInt128 decoded));
            Assert.Equal(value, decoded);

            Assert.True(Base62.TryEncode(value, utf8));
            Assert.Equal(encoded, Encoding.ASCII.GetString(utf8));
            Assert.True(Base62.TryDecodeUInt128(utf8, out decoded));
            Assert.Equal(value, decoded);
        }
    }

    [Fact]
    public void RoundTrip64EdgesAndRandom()
    {
        Span<byte> utf8 = stackalloc byte[Base62.Width64];
        foreach (ulong value in Edge64.Concat(Random64(2000)))
        {
            string encoded = Base62.Encode(value);
            Assert.Equal(Base62.Width64, encoded.Length);
            Assert.Equal(ReferenceEncode(value, 11), encoded);
            Assert.True(Base62.TryDecodeUInt64(encoded, out ulong decoded));
            Assert.Equal(value, decoded);

            Assert.True(Base62.TryEncode(value, utf8));
            Assert.Equal(encoded, Encoding.ASCII.GetString(utf8));
            Assert.True(Base62.TryDecodeUInt64(utf8, out decoded));
            Assert.Equal(value, decoded);
        }
    }

    [Fact]
    public void OrderingSortedValuesGiveSortedStrings128()
    {
        UInt128[] values = [.. Edge128.Concat(Random128(2000)).Order()];
        string[] encoded = [.. values.Select(v => Base62.Encode(v))];
        Assert.Equal(encoded.Order(StringComparer.Ordinal), encoded);
    }

    [Fact]
    public void OrderingSortedValuesGiveSortedStrings64()
    {
        ulong[] values = [.. Edge64.Concat(Random64(2000)).Order()];
        string[] encoded = [.. values.Select(v => Base62.Encode(v))];
        Assert.Equal(encoded.Order(StringComparer.Ordinal), encoded);
    }

    [Fact]
    public void GuidUsesBigEndianByteOrder()
    {
        var guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        UInt128 expected = new(0x0011223344556677UL, 0x8899AABBCCDDEEFFUL);
        Assert.Equal(expected, Base62.ToUInt128(guid));
        Assert.Equal(guid, Base62.FromUInt128(expected));
        Assert.Equal(Base62.Encode(expected), Base62.Encode(guid));
        Assert.True(Base62.TryDecodeGuid(Base62.Encode(guid), out Guid decoded));
        Assert.Equal(guid, decoded);
    }

    [Fact]
    public void DecodeRejectsOverflow()
    {
        Assert.False(Base62.TryDecodeUInt128(new string('z', 22), out _));
        Assert.False(Base62.TryDecodeUInt128(ReferenceEncode((BigInteger)UInt128.MaxValue + 1, 22), out _));
        Assert.False(Base62.TryDecodeUInt64(new string('z', 11), out _));
        Assert.False(Base62.TryDecodeUInt64(ReferenceEncode((BigInteger)ulong.MaxValue + 1, 11), out _));
        Assert.False(Base62.TryDecodeGuid(new string('z', 22), out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    [InlineData(23)]
    public void DecodeRejectsWrongLength128(int length)
    {
        Assert.False(Base62.TryDecodeUInt128(new string('1', length), out _));
        Assert.False(Base62.TryDecodeUInt128(Encoding.ASCII.GetBytes(new string('1', length)), out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(12)]
    public void DecodeRejectsWrongLength64(int length) =>
        Assert.False(Base62.TryDecodeUInt64(new string('1', length), out _));

    [Theory]
    [InlineData('-')]
    [InlineData('_')]
    [InlineData(' ')]
    [InlineData('/')]
    [InlineData(':')]
    [InlineData('@')]
    [InlineData('[')]
    [InlineData('`')]
    [InlineData('{')]
    [InlineData('\0')]
    [InlineData('é')]
    [InlineData('İ')] // 'İ' (low byte 0x30 == '0')
    public void DecodeRejectsInvalidCharacters(char bad)
    {
        foreach (int position in new[] { 0, 5, 21 })
        {
            char[] chars = new string('1', 22).ToCharArray();
            chars[position] = bad;
            Assert.False(Base62.TryDecodeUInt128(chars, out _));
        }

        char[] chars64 = new string('1', 11).ToCharArray();
        chars64[10] = bad;
        Assert.False(Base62.TryDecodeUInt64(chars64, out _));
    }

    [Fact]
    public void DecodeRejectsNonAsciiUtf8Bytes()
    {
        byte[] bytes = Encoding.ASCII.GetBytes(new string('1', 22));
        bytes[3] = 0xB0; // 0x30 | 0x80
        Assert.False(Base62.TryDecodeUInt128(bytes, out _));
    }

    [Fact]
    public void EncodeFailsWhenDestinationTooSmall()
    {
        Assert.False(Base62.TryEncode(UInt128.One, new char[21]));
        Assert.False(Base62.TryEncode(1UL, new byte[10]));
    }

    private static IEnumerable<UInt128> Random128(int count)
    {
        var random = new Random(42);
        for (int i = 0; i < count; i++)
        {
            // Vary magnitude so short and long encodings are both covered.
            UInt128 value = new((ulong)random.NextInt64() ^ ((ulong)random.Next() << 33), (ulong)random.NextInt64());
            yield return value >> random.Next(128);
        }
    }

    private static IEnumerable<ulong> Random64(int count)
    {
        var random = new Random(7);
        for (int i = 0; i < count; i++)
        {
            yield return ((ulong)random.NextInt64() << 1 | (uint)random.Next(2)) >> random.Next(64);
        }
    }

    private static string ReferenceEncode(BigInteger value, int width)
    {
        var sb = new StringBuilder();
        while (value > 0)
        {
            sb.Insert(0, Base62.Alphabet[(int)(value % 62)]);
            value /= 62;
        }

        return sb.ToString().PadLeft(width, '0');
    }
}
