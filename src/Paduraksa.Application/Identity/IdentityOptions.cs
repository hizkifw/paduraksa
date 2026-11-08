namespace Paduraksa.Application.Identity;

/// <summary>
/// The knobs of the identity services (PLAN §10.1). The server binds them from <c>Paduraksa:Admin</c> and validates the bounds;
/// the defaults are the values it ships with.
/// </summary>
public sealed class IdentityOptions
{
    /// <summary>Consecutive wrong passwords or codes before an account is locked.</summary>
    public int MaxFailedAttempts { get; init; } = 5;

    /// <summary>How long a locked account refuses sign-in.</summary>
    public TimeSpan LockoutDuration { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The absolute lifetime of a session, whatever its activity.</summary>
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromHours(12);

    /// <summary>A session unused for this long ends.</summary>
    public TimeSpan SessionIdleTimeout { get; init; } = TimeSpan.FromHours(2);

    /// <summary>How long a person has to give the second factor after the password.</summary>
    public TimeSpan PartialSessionLifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The shortest password accepted (characters). There are no composition rules.</summary>
    public int MinPasswordLength { get; init; } = 12;

    /// <summary>PBKDF2-HMAC-SHA-512 iterations for new password hashes (OWASP 2023: 210 000). Hashes made with fewer are rehashed at sign-in.</summary>
    public int PasswordIterations { get; init; } = 210_000;

    /// <summary>The least interval between writes of a token's last-used or a session's last-seen time.</summary>
    public TimeSpan TouchInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The issuer shown by authenticator apps.</summary>
    public string TotpIssuer { get; init; } = "Paduraksa";

    /// <summary>The longest password accepted: a bound on what one request can cost.</summary>
    public const int MaxPasswordLength = 1024;

    /// <summary>The number of recovery codes issued with a TOTP enrolment.</summary>
    public const int RecoveryCodeCount = 10;
}
