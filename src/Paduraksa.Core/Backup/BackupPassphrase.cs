namespace Paduraksa.Core.Backup;

/// <summary>How good a backup passphrase looks, from <see cref="BackupPassphrase.Rate"/>.</summary>
public enum PassphraseStrength
{
    /// <summary>Below the minimum: a backup can't be made with it.</summary>
    TooShort = 0,

    /// <summary>Allowed, but the archive is only as safe as this is hard to guess: a few more characters, or a few words, help.</summary>
    Fair = 1,

    /// <summary>Long enough that guessing it offline is not realistic against the Argon2id cost.</summary>
    Good = 2,
}

/// <summary>
/// What a backup passphrase must satisfy, one rule for the CLI, the API and the dashboard. The archive is only as strong as the
/// passphrase, and anybody holding a copy of the file can guess offline, so the minimum is length (no composition rules, as for
/// passwords: they make people predictable, not safe). The passphrase is used as typed, as UTF-8, with no normalization.
/// </summary>
public static class BackupPassphrase
{
    /// <summary>The fewest characters (Unicode scalar values) a passphrase has.</summary>
    public const int MinLength = 12;

    /// <summary>The most: it is input to a KDF, so one request must not be able to send megabytes.</summary>
    public const int MaxLength = 1024;

    /// <summary>The length from which <see cref="Rate"/> says <see cref="PassphraseStrength.Good"/>.</summary>
    public const int GoodLength = 20;

    /// <summary>The length in Unicode scalar values (an emoji is one, as in the account passwords).</summary>
    public static int Length(ReadOnlySpan<char> passphrase)
    {
        var length = 0;
        foreach (var _ in passphrase.EnumerateRunes())
        {
            length++;
        }

        return length;
    }

    /// <summary>Why <paramref name="passphrase"/> is not acceptable, or null if it is.</summary>
    public static string? Check(ReadOnlySpan<char> passphrase)
    {
        if (passphrase.IsEmpty)
        {
            return "A passphrase is required.";
        }

        if (passphrase.Length > 2 * MaxLength)
        {
            return $"The passphrase must be at most {MaxLength} characters.";
        }

        var length = Length(passphrase);
        return length < MinLength
            ? $"The passphrase must be at least {MinLength} characters."
            : length > MaxLength ? $"The passphrase must be at most {MaxLength} characters." : null;
    }

    /// <summary>A rough rating by length alone: the only property that can be judged without a dictionary.</summary>
    public static PassphraseStrength Rate(ReadOnlySpan<char> passphrase)
    {
        var length = passphrase.Length > 2 * MaxLength ? int.MaxValue : Length(passphrase);
        return length < MinLength ? PassphraseStrength.TooShort : length >= GoodLength ? PassphraseStrength.Good : PassphraseStrength.Fair;
    }
}
