namespace Paduraksa.Dashboard.AccountArea;

/// <summary>
/// A hint at how strong a new password is, for the meter under the field: length counts most (the only rule the service enforces is
/// a minimum length), then variety. A hint only.
/// </summary>
public static class PasswordStrength
{
    /// <summary>0 (too short) to 4 (strong), with a word for it.</summary>
    public static (int Level, string Label) Of(string password, int minLength)
    {
        ArgumentNullException.ThrowIfNull(password);
        int length = password.EnumerateRunes().Count();
        if (length < minLength)
        {
            return (0, $"Too short: {minLength - length} more {(minLength - length == 1 ? "character" : "characters")}");
        }

        int kinds = (password.Any(char.IsLower) ? 1 : 0) + (password.Any(char.IsUpper) ? 1 : 0) + (password.Any(char.IsDigit) ? 1 : 0)
            + (password.Any(c => !char.IsLetterOrDigit(c)) ? 1 : 0);
        int distinct = password.Distinct().Count();
        int score = (length >= minLength + 8 ? 2 : length >= minLength + 4 ? 1 : 0) + (kinds >= 3 ? 1 : 0) + (distinct >= 10 ? 1 : 0);
        return score switch
        {
            <= 0 => (1, "Weak: longer is better"),
            1 => (2, "Fair"),
            2 => (3, "Good"),
            _ => (4, "Strong"),
        };
    }
}
