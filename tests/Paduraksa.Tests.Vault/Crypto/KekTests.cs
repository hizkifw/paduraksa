using System.Security.Cryptography;
using Paduraksa.Vault.Crypto;

namespace Paduraksa.Tests.Vault.Crypto;

public sealed class KekTests
{
    [Fact]
    public void EncodeThenParseRoundTrips()
    {
        using var kek = Kek.Generate();

        using var parsed = Kek.Parse(kek.Encode());

        Assert.Equal(kek.Bytes.ToArray(), parsed.Bytes.ToArray());
        Assert.Equal(kek.Id, parsed.Id);
        Assert.Equal(kek.CheckValue, parsed.CheckValue);
        Assert.Equal(44, kek.Encode().Length);
    }

    [Fact]
    public void WriteEncodedFillsABufferWithoutMakingAString()
    {
        using var kek = Kek.Generate();
        var buffer = new byte[Kek.TextLength + 3];

        kek.WriteEncoded(buffer);

        Assert.Equal(kek.Encode(), System.Text.Encoding.ASCII.GetString(buffer, 0, Kek.TextLength));
        Assert.Throws<ArgumentException>(() => kek.WriteEncoded(new byte[Kek.TextLength - 1]));
    }

    [Fact]
    public void SurroundingWhitespaceIsIgnored()
    {
        using var kek = Kek.Generate();

        using var parsed = Kek.Parse($" \n{kek.Encode()}\r\n");

        Assert.Equal(kek.Id, parsed.Id);
    }

    [Fact]
    public void AnyOtherKeyHasAnotherIdAndCheckValue()
    {
        using var a = Kek.Generate();
        using var b = Kek.Generate();

        Assert.NotEqual(a.Id, b.Id);
        Assert.NotEqual(a.CheckValue, b.CheckValue);
        Assert.True(a.Matches(a.CheckValue));
        Assert.False(a.Matches(b.CheckValue));
        Assert.Equal(Kek.IdLength, a.Id.Length);
        Assert.Equal(32, a.CheckValue.Length);
    }

    [Fact]
    public void IdAndCheckValueRevealNothingOfTheKey()
    {
        using var kek = Kek.Generate();

        Assert.True(kek.CheckValue.AsSpan().IndexOf(kek.Bytes) < 0);
        Assert.NotEqual(SHA256.HashData(kek.Bytes), kek.CheckValue);
        Assert.DoesNotContain(kek.Encode(), kek.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DisposeZeroesTheKey()
    {
        var kek = Kek.Generate();
        var bytes = kek.Bytes;
        Assert.True(bytes.IndexOfAnyExcept((byte)0) >= 0);

        kek.Dispose();

        Assert.True(bytes.IndexOfAnyExcept((byte)0) < 0);
    }

    public static TheoryData<string> Rejected => new()
    {
        { "" },
        { "   " },
        { "not base64 at all" },
        { Convert.ToBase64String(new byte[31]) },
        { Convert.ToBase64String(Enumerable.Repeat((byte)7, 33).ToArray()) },
        { Convert.ToBase64String(Enumerable.Repeat((byte)7, 64).ToArray()) },
        { Convert.ToHexString(Enumerable.Repeat((byte)7, 32).ToArray()) },
        // base64url instead of base64 (bytes chosen so the alphabets differ).
        { Convert.ToBase64String(Enumerable.Repeat((byte)0xFB, 32).ToArray()).Replace('+', '-').Replace('/', '_') },
        // Whitespace inside the text.
        { Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()).Insert(10, " ") },
        // All zeros: only a mistake produces it.
        { Convert.ToBase64String(new byte[32]) },
        // Non-canonical: same 32 bytes but non-zero padding bits in the last character.
        { Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray())[..42] + "F=" },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public void ParseRefusesAnythingButBase64Of32Bytes(string text)
    {
        var ex = Assert.Throws<FormatException>(() => Kek.Parse(text));

        // The error says what is wanted, never what was given.
        if (text.Trim().Length > 0)
        {
            Assert.DoesNotContain(text.Trim(), ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FromBytesNeedsExactly32()
    {
        Assert.Throws<FormatException>(() => Kek.FromBytes(new byte[16]));
        Assert.Throws<FormatException>(() => Kek.FromBytes(new byte[33]));
    }
}
