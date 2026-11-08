using System.Globalization;

namespace Paduraksa.Application.Identity;

/// <summary>
/// What a new password must satisfy: a minimum length and a maximum (so one request cannot ask for a megabyte of PBKDF2 input).
/// No composition rules: they make people predictable, not safe (NIST SP 800-63B). Length is one measure everywhere
/// (<see cref="Length"/>): Unicode scalar values (runes), so an emoji is one character and a letter with combining marks is several.
/// </summary>
internal sealed class PasswordPolicy(int minLength)
{
    /// <summary>The length of <paramref name="password"/> in runes.</summary>
    public static int Length(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var length = 0;
        foreach (var _ in password.EnumerateRunes())
        {
            length++;
        }

        return length;
    }

    /// <summary>True for no password or one above the maximum; the same measure <see cref="Check"/> applies, cheap to refuse early.</summary>
    public static bool IsEmptyOrTooLong(string? password) =>
        string.IsNullOrEmpty(password)
        || password.Length > 2 * IdentityOptions.MaxPasswordLength // more than that many UTF-16 units is more than the maximum runes
        || Length(password) > IdentityOptions.MaxPasswordLength;

    /// <summary>Why <paramref name="password"/> is not acceptable, or null if it is.</summary>
    public string? Check(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return "A password is required.";
        }

        var length = password.Length > 2 * IdentityOptions.MaxPasswordLength ? int.MaxValue : Length(password);
        if (length < minLength)
        {
            return string.Create(CultureInfo.InvariantCulture, $"The password must be at least {minLength} characters.");
        }

        return length > IdentityOptions.MaxPasswordLength
            ? string.Create(CultureInfo.InvariantCulture, $"The password must be at most {IdentityOptions.MaxPasswordLength} characters.")
            : null;
    }
}
