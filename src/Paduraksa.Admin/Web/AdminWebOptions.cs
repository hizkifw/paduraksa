namespace Paduraksa.Admin.Web;

/// <summary>
/// Where the browser side of the admin lives (PLAN §12): the dashboard's pages, the form endpoints its sign-in pages post to, and its
/// circuit hub. The dashboard owns the names; the composition root hands them to <see cref="AdminWebExtensions.AddPaduraksaAdminWeb"/>, so
/// neither project references the other.
/// </summary>
public sealed class AdminWebOptions
{
    /// <summary>The page a signed-in browser lands on (and where an unsafe <c>returnUrl</c> is replaced by).</summary>
    public required string HomePage { get; init; }

    /// <summary>The sign-in page (username and password).</summary>
    public required string SignInPage { get; init; }

    /// <summary>The second-factor page (a TOTP code or a recovery code).</summary>
    public required string SecondFactorPage { get; init; }

    /// <summary>The first-run setup page.</summary>
    public required string SetupPage { get; init; }

    /// <summary>Where the setup form sends the new Owner once signed in (the rest of the first-run wizard); null for <see cref="HomePage"/>.</summary>
    public string? SetupCompletedPage { get; init; }

    /// <summary>Where the sign-in page posts the username and password.</summary>
    public required string SignInAction { get; init; }

    /// <summary>Where the second-factor page posts the code.</summary>
    public required string SecondFactorAction { get; init; }

    /// <summary>Where the dashboard's sign-out button posts.</summary>
    public required string SignOutAction { get; init; }

    /// <summary>Where the setup page posts the setup token and the Owner.</summary>
    public required string SetupAction { get; init; }

    /// <summary>The circuit hub's path prefix (Blazor's <c>/_blazor</c>): signed-in callers only, same-origin on every method.</summary>
    public required string CircuitHubPath { get; init; }

    /// <summary>Path prefixes of the static files the sign-in and setup pages need (open during setup).</summary>
    public required IReadOnlyList<string> StaticAssetPathPrefixes { get; init; }

    /// <summary>The CSP of the dashboard's HTML documents.</summary>
    public required string ContentSecurityPolicy { get; init; }
}

/// <summary>
/// The names the dashboard's sign-in forms and the endpoints behind them agree on: the form fields, the query parameters a redirect
/// carries, and the error codes it can name (the API's problem codes, PLAN §11.1, plus <see cref="PasswordMismatch"/>).
/// </summary>
public static class AdminWebForm
{
    /// <summary>The query parameter naming why a step failed: one of <see cref="ErrorCodes"/>.</summary>
    public const string ErrorParameter = "error";

    /// <summary>The query parameter carrying a short notice for the sign-in page (<see cref="SignedOutNotice"/>).</summary>
    public const string NoticeParameter = "notice";

    /// <summary>The notice after signing out.</summary>
    public const string SignedOutNotice = "signedOut";

    /// <summary>The form field and query parameter carrying the local page to return to after signing in.</summary>
    public const string ReturnUrl = "returnUrl";

    /// <summary>The query parameter that opens the second-factor page on the recovery-code form.</summary>
    public const string RecoveryParameter = "recovery";

    public const string Username = "username";

    public const string Password = "password";

    public const string Code = "code";

    public const string RecoveryCode = "recoveryCode";

    public const string SetupToken = "setupToken";

    public const string DisplayName = "displayName";

    public const string ConfirmPassword = "confirmPassword";

    /// <summary>The setup form's two passwords differ (checked before anything else; the API has no such field).</summary>
    public const string PasswordMismatch = "setup.passwordMismatch";

    /// <summary>Every code a redirect's <see cref="ErrorParameter"/> can carry.</summary>
    public static IReadOnlyList<string> ErrorCodes { get; } =
    [
        Api.ApiCodes.Auth.InvalidCredentials,
        Api.ApiCodes.Auth.InvalidCode,
        Api.ApiCodes.Auth.SessionExpired,
        Api.ApiCodes.Auth.RateLimited,
        Api.ApiCodes.Auth.Busy,
        Api.ApiCodes.Csrf.Refused,
        Api.ApiCodes.Setup.Complete,
        Api.ApiCodes.Setup.InvalidToken,
        Api.ApiCodes.Identity.Refused,
        PasswordMismatch,
    ];
}
