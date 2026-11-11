using Paduraksa.Core.Security;

namespace Paduraksa.Tests.Unit.Security;

public sealed class PasswordHashTests
{
    [Fact]
    public void AHashVerifiesItsPasswordAndNothingElse()
    {
        var stored = PasswordHash.Create("correct horse");
        Assert.True(PasswordHash.TryParse(stored, out var parsed));

        Assert.True(PasswordHash.Verify("correct horse"u8, parsed));
        Assert.False(PasswordHash.Verify("correct hors"u8, parsed));
        Assert.False(PasswordHash.Verify(""u8, parsed));
    }

    [Fact]
    public void TheCostAndTheSaltAreInTheStringAndEveryHashHasItsOwnSalt()
    {
        var first = PasswordHash.Create("same");
        var second = PasswordHash.Create("same");

        Assert.StartsWith($"$pbkdf2-sha256$i={PasswordHash.DefaultIterations}$", first, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
        Assert.True(PasswordHash.TryParse(first, out var parsed));
        Assert.Equal(PasswordHash.DefaultIterations, parsed.Iterations);
        Assert.Equal(PasswordHash.SaltLength, parsed.Salt.Length);
        Assert.Equal(PasswordHash.HashLength, parsed.Hash.Length);
        Assert.DoesNotContain("same", first, StringComparison.Ordinal);
    }

    [Fact]
    public void AHashMadeAtAnotherCostStillVerifies()
    {
        var stored = PasswordHash.Create("pw", PasswordHash.MinIterations);

        Assert.True(PasswordHash.TryParse(stored, out var parsed));
        Assert.Equal(PasswordHash.MinIterations, parsed.Iterations);
        Assert.True(PasswordHash.Verify("pw"u8, parsed));
    }

    [Theory]
    [InlineData(99_999)]
    [InlineData(10_000_001)]
    public void ACostOutsideTheAcceptedRangeIsRefusedWhenMadeAndWhenRead(int iterations)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordHash.Create("pw", iterations));
        var stored = PasswordHash.Create("pw", PasswordHash.MinIterations).Replace($"i={PasswordHash.MinIterations}", $"i={iterations}", StringComparison.Ordinal);

        Assert.False(PasswordHash.IsValid(stored));
    }

    [Fact]
    public void WhatThisBuildDidNotWriteIsNotAHash()
    {
        var stored = PasswordHash.Create("pw");
        var parts = stored.Split('$');

        Assert.False(PasswordHash.IsValid(null));
        Assert.False(PasswordHash.IsValid(string.Empty));
        Assert.False(PasswordHash.IsValid("plain password"));
        Assert.False(PasswordHash.IsValid("$2y$10$abcdefghijklmnopqrstuvabcdefghijklmnopqrstuvwxyz0123456"));
        Assert.False(PasswordHash.IsValid(stored + "$extra"));
        Assert.False(PasswordHash.IsValid(stored + "="));
        Assert.False(PasswordHash.IsValid(stored[..^4]));
        Assert.False(PasswordHash.IsValid($"${parts[1]}$i=0x30d40${parts[3]}${parts[4]}"));
        Assert.False(PasswordHash.IsValid($"$pbkdf2-sha1$i=210000${parts[3]}${parts[4]}"));
    }

    [Fact]
    public void ASecondSpellingOfTheSameBytesIsRefused()
    {
        var stored = PasswordHash.Create("pw");
        var parts = stored.Split('$');
        var padded = $"${parts[1]}${parts[2]}${parts[3]}${parts[4]}=";

        Assert.False(PasswordHash.IsValid(padded));

        // A 16-byte salt ends in a character whose last two bits are unused: set them and it decodes to the same bytes.
        var loose = parts[3][..^1] + "B";
        Assert.False(PasswordHash.IsValid($"${parts[1]}${parts[2]}${loose}${parts[4]}"));
    }
}
