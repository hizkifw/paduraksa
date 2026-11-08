using Paduraksa.Application.Identity;

namespace Paduraksa.Tests.Unit.Identity;

public sealed class PasswordHasherTests
{
    private static readonly PasswordHasher Hasher = new(1_000);

    [Fact]
    public void AHashDescribesItselfAndVerifies()
    {
        var hash = Hasher.Hash("correct horse battery staple");

        var parts = hash.Split('$');
        Assert.Equal(["", "pbkdf2-sha512", "v1", "i=1000"], parts[..4]);
        Assert.Equal(22, parts[4].Length); // 16-byte salt, unpadded base64
        Assert.Equal(86, parts[5].Length); // 64-byte hash, unpadded base64
        Assert.Equal(new PasswordCheck(true, false), Hasher.Verify("correct horse battery staple", hash));
    }

    [Fact]
    public void ADifferentPasswordOrCaseDoesNotMatch()
    {
        var hash = Hasher.Hash("correct horse battery staple");

        Assert.False(Hasher.Verify("Correct horse battery staple", hash).Matches);
        Assert.False(Hasher.Verify(string.Empty, hash).Matches);
    }

    [Fact]
    public void EachHashGetsItsOwnSalt()
    {
        Assert.NotEqual(Hasher.Hash("same password"), Hasher.Hash("same password"));
    }

    [Fact]
    public void UnicodePasswordsRoundTrip()
    {
        var hash = Hasher.Hash("pässwörd-密码-🔑");

        Assert.True(Hasher.Verify("pässwörd-密码-🔑", hash).Matches);
        Assert.False(Hasher.Verify("passwoerd-密码-🔑", hash).Matches);
    }

    [Fact]
    public void ALowerCostHashVerifiesAndAsksForARehash()
    {
        var old = new PasswordHasher(1_000).Hash("correct horse battery staple");
        var stronger = new PasswordHasher(2_000);

        Assert.Equal(new PasswordCheck(true, true), stronger.Verify("correct horse battery staple", old));
        Assert.Equal(new PasswordCheck(false, false), stronger.Verify("wrong", old)); // never a rehash for a wrong password
        Assert.Contains("$i=2000$", stronger.Hash("x"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plaintext")]
    [InlineData("$pbkdf2-sha512$v1$i=1000$AAAA$BBBB")]
    [InlineData("$pbkdf2-sha256$v1$i=1000$AAAAAAAAAAAAAAAAAAAAAA$AAAA")]
    [InlineData("$pbkdf2-sha512$v2$i=1000$AAAAAAAAAAAAAAAAAAAAAA$AAAA")]
    [InlineData("$pbkdf2-sha512$v1$i=999999999999$AAAAAAAAAAAAAAAAAAAAAA$AAAA")]
    [InlineData("$pbkdf2-sha512$v1$i=1$AAAAAAAAAAAAAAAAAAAAAA$AAAA")]
    [InlineData("$pbkdf2-sha512$v1$i=1000$!!!$???")]
    [InlineData("$pbkdf2-sha512$v1$i=-5$AAAAAAAAAAAAAAAAAAAAAA$AAAA")]
    public void AnythingThatIsNotAHashNeverMatches(string stored)
    {
        Assert.Equal(new PasswordCheck(false, false), Hasher.Verify("anything", stored));
    }

    [Fact]
    public void AStoredCostBeyondTheCeilingIsRefusedBeforeItCanBeUsedToBurnCpu()
    {
        var hash = Hasher.Hash("x").Replace("i=1000", "i=2000000000", StringComparison.Ordinal);

        Assert.False(Hasher.Verify("x", hash).Matches);
    }

    [Fact]
    public void UnknownUsersSpendTheSameWorkAsKnownOnes()
    {
        // Same call path and the same cost: a derivation against a throwaway hash. (Timing itself is not asserted: it is
        // the structure that keeps them equal.)
        Hasher.VerifyUnknown("anything");
        Hasher.VerifyUnknown(string.Empty);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(20_000_001)]
    public void TheCostIsBounded(int iterations)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PasswordHasher(iterations));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("short", false)]
    [InlineData("exactly12chr", true)]
    [InlineData("aaaaaaaaaaaa", true)] // no composition rules
    [InlineData("🔑🔑🔑🔑🔑🔑🔑🔑🔑🔑🔑", false)] // 11 characters, however many UTF-16 units
    [InlineData("🔑🔑🔑🔑🔑🔑🔑🔑🔑🔑🔑🔑", true)]
    public void ThePolicyIsALengthAndNothingElse(string password, bool acceptable)
    {
        Assert.Equal(acceptable, new PasswordPolicy(12).Check(password) is null);
    }

    [Fact]
    public void ThePolicyBoundsTheLengthAbove()
    {
        var policy = new PasswordPolicy(12);

        Assert.Null(policy.Check(new string('a', IdentityOptions.MaxPasswordLength)));
        Assert.NotNull(policy.Check(new string('a', IdentityOptions.MaxPasswordLength + 1)));
        Assert.NotNull(policy.Check(null));
    }
}
