using System.Security.Cryptography;
using Paduraksa.Vault.Crypto;

namespace Paduraksa.Tests.Vault.Crypto;

/// <summary>The format, AAD binding, tamper detection and rewrap of <see cref="EnvelopeCipher"/> (PLAN §8.3). Offsets are those of format 1.</summary>
public sealed class EnvelopeCipherTests : IDisposable
{
    private static readonly SealContext Context = new("sec_0001", "dns-api-token", 1);

    private readonly Kek _kek = Kek.Generate();

    public void Dispose() => _kek.Dispose();

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(1000)]
    public void SealThenOpenRoundTrips(int length)
    {
        var plaintext = RandomNumberGenerator.GetBytes(length);

        var sealedValue = EnvelopeCipher.Seal(_kek, plaintext, Context);
        using var opened = EnvelopeCipher.Open(_kek, sealedValue, Context);

        Assert.Equal(length + EnvelopeCipher.Overhead, sealedValue.Length);
        Assert.Equal(plaintext, opened.Span.ToArray());
    }

    [Fact]
    public void SealingTheSamePlaintextTwiceGivesDifferentValues()
    {
        var plaintext = "same value"u8.ToArray();

        var first = EnvelopeCipher.Seal(_kek, plaintext, Context);
        var second = EnvelopeCipher.Seal(_kek, plaintext, Context);

        // A fresh DEK and fresh nonces each time: nothing repeats, not the wrapped DEK, nor the ciphertext.
        Assert.NotEqual(first.AsSpan(9, 12).ToArray(), second.AsSpan(9, 12).ToArray());
        Assert.NotEqual(first.AsSpan(21, 32).ToArray(), second.AsSpan(21, 32).ToArray());
        Assert.NotEqual(first.AsSpan(81, plaintext.Length).ToArray(), second.AsSpan(81, plaintext.Length).ToArray());
    }

    [Fact]
    public void ValueStartsWithFormatAndKekId()
    {
        var sealedValue = EnvelopeCipher.Seal(_kek, [1, 2, 3], Context);

        Assert.Equal(EnvelopeCipher.Format1, sealedValue[0]);
        Assert.Equal(_kek.Id, sealedValue.AsSpan(1, Kek.IdLength).ToArray());
        Assert.Equal(_kek.Id, EnvelopeCipher.KekIdOf(sealedValue).ToArray());
    }

    [Fact]
    public void CiphertextDoesNotContainThePlaintext()
    {
        var plaintext = "super-secret-token-value-123456"u8.ToArray();

        var sealedValue = EnvelopeCipher.Seal(_kek, plaintext, Context);

        Assert.True(sealedValue.AsSpan().IndexOf(plaintext) < 0);
    }

    public static TheoryData<string, string, int> OtherContexts => new()
    {
        { "sec_0002", "dns-api-token", 1 },
        { "sec_0001", "tsig-key", 1 },
        { "sec_0001", "dns-api-token", 2 },
        { "acct_0001", "dns-api-token", 1 },
    };

    [Theory]
    [MemberData(nameof(OtherContexts))]
    public void AValueDoesNotOpenForAnotherRowKindOrVersion(string id, string purpose, int version)
    {
        var sealedValue = EnvelopeCipher.Seal(_kek, "value"u8, Context);

        Assert.ThrowsAny<CryptographicException>(() => EnvelopeCipher.Open(_kek, sealedValue, new SealContext(id, purpose, version)));
    }

    [Fact]
    public void TheContextIsLengthPrefixedSoFieldsCannotShiftIntoEachOther()
    {
        var sealedValue = EnvelopeCipher.Seal(_kek, "value"u8, new SealContext("sec_1", "ab", 1));

        // The same concatenated text split differently is still another context.
        Assert.ThrowsAny<CryptographicException>(() => EnvelopeCipher.Open(_kek, sealedValue, new SealContext("sec_1a", "b", 1)));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(21)]
    [InlineData(53)]
    [InlineData(69)]
    [InlineData(81)]
    public void AnyChangedByteInTheValueFailsToOpen(int offset)
    {
        var sealedValue = EnvelopeCipher.Seal(_kek, "value"u8, Context);
        sealedValue[offset] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => EnvelopeCipher.Open(_kek, sealedValue, Context));
    }

    [Fact]
    public void AChangedDataTagFailsToOpen()
    {
        var sealedValue = EnvelopeCipher.Seal(_kek, "value"u8, Context);
        sealedValue[^1] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => EnvelopeCipher.Open(_kek, sealedValue, Context));
    }

    [Fact]
    public void ATruncatedOrExtendedValueFailsToOpen()
    {
        var sealedValue = EnvelopeCipher.Seal(_kek, "value"u8, Context);

        Assert.ThrowsAny<CryptographicException>(() => EnvelopeCipher.Open(_kek, sealedValue.AsSpan(..^1), Context));
        Assert.ThrowsAny<CryptographicException>(() => EnvelopeCipher.Open(_kek, [.. sealedValue, 0], Context));
        Assert.ThrowsAny<CryptographicException>(() => EnvelopeCipher.Open(_kek, sealedValue.AsSpan(..10), Context));
        Assert.ThrowsAny<CryptographicException>(() => EnvelopeCipher.Open(_kek, [], Context));
    }

    [Fact]
    public void AnUnknownFormatIsRefused()
    {
        var sealedValue = EnvelopeCipher.Seal(_kek, "value"u8, Context);
        sealedValue[0] = 2;

        var ex = Assert.ThrowsAny<CryptographicException>(() => EnvelopeCipher.Open(_kek, sealedValue, Context));
        Assert.Contains("format", ex.Message, StringComparison.Ordinal);
        Assert.Empty(EnvelopeCipher.KekIdOf(sealedValue).ToArray());
    }

    [Fact]
    public void AnotherKekIsToldApart()
    {
        using var other = Kek.Generate();
        var sealedValue = EnvelopeCipher.Seal(_kek, "value"u8, Context);

        Assert.Throws<KekMismatchException>(() => EnvelopeCipher.Open(other, sealedValue, Context));
    }

    [Fact]
    public void ADekCannotBeMovedToAnotherRowEvenWithTheDataKept()
    {
        // Row A's wrapped DEK grafted onto row B's data: the wrap's AAD names row A, so it does not unwrap for B.
        var a = EnvelopeCipher.Seal(_kek, "value-a"u8, new SealContext("sec_a", "dns-api-token", 1));
        var b = EnvelopeCipher.Seal(_kek, "value-b"u8, new SealContext("sec_b", "dns-api-token", 1));
        a.AsSpan(9, 60).CopyTo(b.AsSpan(9, 60));

        Assert.ThrowsAny<CryptographicException>(() => EnvelopeCipher.Open(_kek, b, new SealContext("sec_b", "dns-api-token", 1)));
    }

    [Fact]
    public void RewrapMovesTheValueToTheNewKekAndKeepsTheData()
    {
        using var newKek = Kek.Generate();
        var plaintext = RandomNumberGenerator.GetBytes(40);
        var original = EnvelopeCipher.Seal(_kek, plaintext, Context);

        var rewrapped = EnvelopeCipher.Rewrap(_kek, newKek, original, Context);

        using var opened = EnvelopeCipher.Open(newKek, rewrapped, Context);
        Assert.Equal(plaintext, opened.Span.ToArray());
        Assert.Equal(newKek.Id, EnvelopeCipher.KekIdOf(rewrapped).ToArray());
        Assert.Throws<KekMismatchException>(() => EnvelopeCipher.Open(_kek, rewrapped, Context));
        Assert.Equal(original.AsSpan(69).ToArray(), rewrapped.AsSpan(69).ToArray());
        Assert.NotEqual(original.AsSpan(21, 32).ToArray(), rewrapped.AsSpan(21, 32).ToArray());
    }

    [Fact]
    public void RewrapNeedsTheRightOldKekAndContext()
    {
        using var newKek = Kek.Generate();
        using var wrong = Kek.Generate();
        var original = EnvelopeCipher.Seal(_kek, "value"u8, Context);

        Assert.Throws<KekMismatchException>(() => EnvelopeCipher.Rewrap(wrong, newKek, original, Context));
        Assert.ThrowsAny<CryptographicException>(() => EnvelopeCipher.Rewrap(_kek, newKek, original, Context with { Version = 9 }));
    }

    [Fact]
    public void ABufferIsZeroedWhenDisposed()
    {
        var buffer = SecretBuffer.CopyOf([1, 2, 3, 4]);
        var view = buffer.Fill;
        Assert.Equal(1, view[0]);

        buffer.Dispose();

        Assert.True(view.IndexOfAnyExcept((byte)0) < 0);
    }
}
