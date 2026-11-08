using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Identity;

// The wire shapes of the identity services (PLAN §5.2, §10.1). They carry typed IDs and names, never a hash, a secret reference or
// a stored token. Requests that hold a credential print a redacted ToString, so a stray log line cannot leak one.

/// <summary>An admin user as the admin surfaces see them.</summary>
/// <param name="Id">The user.</param>
/// <param name="Username">The sign-in name, as the user typed it when created.</param>
/// <param name="DisplayName">The name shown in the UI.</param>
/// <param name="Roles">The roles the user holds, in a fixed order.</param>
/// <param name="TwoFactorEnabled">An authenticator is enrolled, so signing in needs a code.</param>
/// <param name="Disabled">The user cannot sign in, and their tokens and sessions are dead.</param>
/// <param name="Locked">Sign-in is refused for now after too many failures.</param>
/// <param name="CreatedAt">When the user was created.</param>
/// <param name="UpdatedAt">When the user was last changed.</param>
public sealed record UserSummary(
    UserId Id,
    string Username,
    string DisplayName,
    IReadOnlyList<Role> Roles,
    bool TwoFactorEnabled,
    bool Disabled,
    bool Locked,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Creates a user.</summary>
/// <param name="Username">3 to 64 characters: letters, digits and <c>. _ - @ +</c>. Unique without regard to case.</param>
/// <param name="DisplayName">Shown in the UI; the username when null or empty.</param>
/// <param name="Password">The initial password (minimum length applies, no other rule).</param>
/// <param name="Roles">At least one.</param>
public sealed record CreateUserRequest(string Username, string? DisplayName, string Password, IReadOnlyList<Role> Roles)
{
    public override string ToString() => $"{nameof(CreateUserRequest)} {{ Username = {Username}, Password = *** }}";
}

/// <summary>Changes a user; a null member stays as it is.</summary>
/// <param name="DisplayName">The new display name.</param>
/// <param name="Roles">The complete new set of roles (not a delta); at least one when given.</param>
/// <param name="Disabled">True disables the user (ending their sessions; their tokens stop working), false re-enables them and clears a lockout.</param>
public sealed record UpdateUserRequest(string? DisplayName = null, IReadOnlyList<Role>? Roles = null, bool? Disabled = null);

/// <summary>Changes the signed-in user's own password.</summary>
/// <param name="CurrentPassword">Proves the person at the keyboard knows it.</param>
/// <param name="NewPassword">The new password.</param>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword)
{
    public override string ToString() => $"{nameof(ChangePasswordRequest)} {{ *** }}";
}

/// <summary>The first step of signing in.</summary>
/// <param name="Username">The sign-in name (case does not matter).</param>
/// <param name="Password">The password.</param>
public sealed record LoginRequest(string Username, string Password)
{
    public override string ToString() => $"{nameof(LoginRequest)} {{ Username = {Username}, Password = *** }}";
}

/// <summary>The second step: exactly one of a TOTP code or a recovery code.</summary>
/// <param name="Code">The six digits from the authenticator.</param>
/// <param name="RecoveryCode">A single-use recovery code.</param>
public sealed record SecondFactorRequest(string? Code = null, string? RecoveryCode = null)
{
    public override string ToString() => $"{nameof(SecondFactorRequest)} {{ *** }}";
}

/// <summary>An active session, for the session list.</summary>
/// <param name="Id">The session (a handle, not the cookie).</param>
/// <param name="ClientIp">Where it was created from.</param>
/// <param name="UserAgent">The browser that created it.</param>
/// <param name="CreatedAt">When it began.</param>
/// <param name="LastSeenAt">When it was last used (to within the touch interval).</param>
/// <param name="ExpiresAt">Its absolute end; it ends earlier if idle too long.</param>
/// <param name="SecondFactorCompleted">False for a session still waiting for the second factor.</param>
/// <param name="Current">The session that asked for the list.</param>
public sealed record SessionSummary(
    SessionId Id,
    string? ClientIp,
    string? UserAgent,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt,
    bool SecondFactorCompleted,
    bool Current);

/// <summary>An API token's public facts. The secret is shown once, when it is issued, and never again.</summary>
/// <param name="Id">The token.</param>
/// <param name="UserId">The user it acts as; it can never do more than they can.</param>
/// <param name="Name">What the owner called it.</param>
/// <param name="Scopes">What it may do: a subset of what its owner may do.</param>
/// <param name="ExpiresAt">When it stops working, or null for never.</param>
/// <param name="CreatedAt">When it was issued.</param>
/// <param name="LastUsedAt">When it was last used (to within the touch interval), or null.</param>
/// <param name="Revoked">It was revoked.</param>
public sealed record ApiTokenSummary(
    ApiTokenId Id,
    UserId UserId,
    string Name,
    IReadOnlyList<Permission> Scopes,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    bool Revoked);

/// <summary>Issues an API token for the signed-in user.</summary>
/// <param name="Name">1 to 128 characters.</param>
/// <param name="Scopes">At least one, each a permission the user holds.</param>
/// <param name="ExpiresAt">In the future, or null for a token that never expires.</param>
public sealed record IssueApiTokenRequest(string Name, IReadOnlyList<Permission> Scopes, DateTimeOffset? ExpiresAt = null);

/// <summary>A newly issued token: the only time its secret is shown.</summary>
/// <param name="Summary">The token's public facts.</param>
/// <param name="Token">The full <c>pdk_…</c> string to present as a Bearer token. Not recoverable later.</param>
public sealed record IssuedApiToken(ApiTokenSummary Summary, string Token)
{
    public override string ToString() => $"{nameof(IssuedApiToken)} {{ Id = {Summary.Id}, Token = *** }}";
}

/// <summary>The start of a TOTP enrollment.</summary>
/// <param name="ProvisioningUri">The <c>otpauth://</c> URI for the QR code: shown once, not stored.</param>
public sealed record TotpEnrollmentStarted(Uri ProvisioningUri)
{
    public override string ToString() => $"{nameof(TotpEnrollmentStarted)} {{ *** }}";
}

/// <summary>Recovery codes, shown once: only their hashes are kept.</summary>
/// <param name="Codes">Single-use codes, e.g. <c>abcd-efgh-jkmn-pqrs</c>.</param>
public sealed record RecoveryCodes(IReadOnlyList<string> Codes)
{
    public override string ToString() => $"{nameof(RecoveryCodes)} {{ {Codes.Count} codes }}";
}

/// <summary>Whether the first Owner exists. Until it does, the admin app serves only the setup flow.</summary>
/// <param name="Complete">An Owner exists.</param>
public sealed record SetupStatus(bool Complete);

/// <summary>A session that was just started, by signing in or by setup. Its cookie travels in <c>Set-Cookie</c> only, never in a body.</summary>
/// <param name="SessionId">The session (a handle, not the cookie).</param>
/// <param name="SecondFactorRequired">The password was right but a second factor is owed: only <c>auth/second-factor</c> accepts this session until it is given.</param>
/// <param name="ExpiresAt">When the session ends (when the partial session ends, if a second factor is owed).</param>
public sealed record SessionStarted(SessionId SessionId, bool SecondFactorRequired, DateTimeOffset ExpiresAt);

/// <summary>Creates the first Owner (PLAN §12.1).</summary>
/// <param name="SetupToken">The one-time token from the server log or <c>paduraksa setup-token</c>.</param>
/// <param name="Username">The Owner's sign-in name.</param>
/// <param name="Password">The Owner's password.</param>
/// <param name="DisplayName">Shown in the UI; the username when null or empty.</param>
public sealed record SetupRequest(string SetupToken, string Username, string Password, string? DisplayName = null)
{
    public override string ToString() => $"{nameof(SetupRequest)} {{ Username = {Username}, *** }}";
}

/// <summary>The result of first-run setup: the new Owner, signed in.</summary>
/// <param name="Owner">The new Owner.</param>
/// <param name="Session">The session the response sets a cookie for; null only if signing the new Owner in failed, in which case they sign in as usual.</param>
public sealed record SetupCompleted(UserSummary Owner, SessionStarted? Session);

/// <summary>Proves the person at the keyboard knows their password, for a sensitive change to their own sign-in.</summary>
/// <param name="Password">The current password.</param>
public sealed record PasswordConfirmation(string Password)
{
    public override string ToString() => $"{nameof(PasswordConfirmation)} {{ *** }}";
}

/// <summary>A code from the authenticator, to confirm an enrollment.</summary>
/// <param name="Code">The six digits.</param>
public sealed record TotpConfirmation(string Code)
{
    public override string ToString() => $"{nameof(TotpConfirmation)} {{ *** }}";
}

/// <summary>Who the admin API thinks the caller is.</summary>
/// <param name="User">The user (for a token, the one it acts as).</param>
/// <param name="Permissions">What the caller may do right now, in a fixed order: for a token, its scopes narrowed to what its user holds.</param>
/// <param name="AuthKind">How the caller authenticated: <c>session</c> or <c>token</c>.</param>
/// <param name="SessionId">The session, for <c>session</c>.</param>
/// <param name="TokenId">The token, for <c>token</c>.</param>
public sealed record CurrentCaller(
    UserSummary User,
    IReadOnlyList<Permission> Permissions,
    string AuthKind,
    SessionId? SessionId = null,
    ApiTokenId? TokenId = null);
