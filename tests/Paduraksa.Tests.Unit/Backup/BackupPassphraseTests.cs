using Paduraksa.Core.Backup;

namespace Paduraksa.Tests.Unit.Backup;

public sealed class BackupPassphraseTests
{
    [Theory]
    [InlineData("", "A passphrase is required.")]
    [InlineData("eleven char", "The passphrase must be at least 12 characters.")]
    public void ATooShortPassphraseIsRefusedWithTheMinimum(string passphrase, string problem) =>
        Assert.Equal(problem, BackupPassphrase.Check(passphrase));

    [Fact]
    public void TheMinimumIsAllowedAndTheMaximumIsEnforced()
    {
        Assert.Null(BackupPassphrase.Check(new string('x', 12)));
        Assert.Null(BackupPassphrase.Check(new string('x', 1024)));
        Assert.Equal("The passphrase must be at most 1024 characters.", BackupPassphrase.Check(new string('x', 1025)));
        Assert.Equal("The passphrase must be at most 1024 characters.", BackupPassphrase.Check(new string('x', 1_000_000)));
    }

    [Fact]
    public void LengthCountsScalarValuesSoAnEmojiIsOneCharacter()
    {
        Assert.Equal(12, BackupPassphrase.Length(string.Concat(Enumerable.Repeat("\U0001F511", 12))));
        Assert.Null(BackupPassphrase.Check(string.Concat(Enumerable.Repeat("\U0001F511", 12))));
        Assert.NotNull(BackupPassphrase.Check(string.Concat(Enumerable.Repeat("\U0001F511", 11))));
    }

    [Theory]
    [InlineData("", PassphraseStrength.TooShort)]
    [InlineData("short", PassphraseStrength.TooShort)]
    [InlineData("twelve chars", PassphraseStrength.Fair)]
    [InlineData("correct horse battery staple", PassphraseStrength.Good)]
    public void TheHintFollowsLength(string passphrase, PassphraseStrength expected) =>
        Assert.Equal(expected, BackupPassphrase.Rate(passphrase));
}
