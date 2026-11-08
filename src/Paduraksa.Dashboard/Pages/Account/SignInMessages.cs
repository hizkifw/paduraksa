using System.Globalization;

namespace Paduraksa.Dashboard.Pages.Account;

/// <summary>
/// What the sign-in and setup pages say for the error code a failed step redirects with (the admin API's problem codes, PLAN §11.1).
/// Only known codes are shown, as fixed text: nothing from the address bar is echoed into the page.
/// </summary>
public static class SignInMessages
{
    private static readonly Dictionary<string, string> Errors = new(StringComparer.Ordinal)
    {
        ["auth.invalidCredentials"] = "The username or password is wrong.",
        ["auth.invalidCode"] = "That code didn't work. Check the time on your device and try the current code.",
        ["auth.sessionExpired"] = "The sign-in took too long. Start again with your password.",
        ["auth.rateLimited"] = "Too many attempts. Wait a minute, then try again.",
        ["auth.busy"] = "The server is busy checking other sign-ins. Try again in a moment.",
        ["csrf.refused"] = "The form expired. Please try again.",
        ["setup.complete"] = "Setup is already complete. Sign in with the Owner account.",
        ["setup.invalidToken"] = "The setup token is wrong. Copy it again from the server log, or run paduraksa setup-token.",
        ["identity.refused"] = "That username or password can't be used. Check the hints below the fields.",
        ["setup.passwordMismatch"] = "The two passwords don't match.",
    };

    private static readonly Dictionary<string, string> Notices = new(StringComparer.Ordinal)
    {
        [DashboardForm.SignedOutNotice] = "You have signed out.",
        [DashboardForm.SessionEndedNotice] = "Your session ended. Sign in again to continue.",
    };

    /// <summary>Every error code this page knows.</summary>
    public static IReadOnlyCollection<string> KnownErrors => Errors.Keys;

    /// <summary>The message for <paramref name="code"/>; a generic one for a code this page does not know; null for none.</summary>
    public static string? Error(string? code) =>
        string.IsNullOrEmpty(code) ? null : Errors.GetValueOrDefault(code, "That didn't work. Please try again.");

    /// <summary>The notice for <paramref name="code"/>, or null (unknown notices are not shown).</summary>
    public static string? Notice(string? code) => code is null ? null : Notices.GetValueOrDefault(code);

    /// <summary>The setup form's password hint.</summary>
    public static string PasswordHint(int minLength) =>
        string.Create(CultureInfo.InvariantCulture, $"At least {minLength} characters. A long passphrase is best; there are no other rules.");

    /// <summary>The username hint (the identity services' rule).</summary>
    public const string UsernameHint = "3 to 64 characters: letters, digits and . _ - @ +";

    /// <summary>The same rule as an HTML <c>pattern</c>.</summary>
    public const string UsernamePattern = "[A-Za-z0-9._@+\\-]{3,64}";

    /// <summary>
    /// <paramref name="returnUrl"/> when it is a path on this site, else null (the forms send it back to the server, which checks it
    /// again; this only keeps a foreign value off the page).
    /// </summary>
    public static string? LocalReturnUrl(string? returnUrl) =>
        returnUrl is { Length: > 0 } && returnUrl[0] == '/' && (returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\'))
            && !returnUrl.Contains('\\', StringComparison.Ordinal) && !returnUrl.AsSpan().ContainsAnyInRange('\0', ' ')
            ? returnUrl
            : null;
}
