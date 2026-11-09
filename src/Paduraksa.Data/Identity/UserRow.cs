using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paduraksa.Core.Model;
using Paduraksa.Data.Conversion;

namespace Paduraksa.Data.Identity;

/// <summary>
/// An admin user. Not part of the config document: users change on their own (a login, a password change) and are
/// audited one by one. <b>Secrets here are hashes or references only</b>: the password as a self-describing PBKDF2
/// string, the TOTP secret as a vault <c>SecretId</c> (the secret itself is sealed in the vault and used through
/// <c>ITotpAuthenticator</c>), never a value.
/// </summary>
internal sealed class UserRow
{
    public UserId Id { get; set; }

    /// <summary>Unique, compared without regard to case (the column's collation), stored as typed.</summary>
    public string Username { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>A versioned PBKDF2 string (<c>PasswordHasher</c> in Application): algorithm, cost, salt and hash in one value.</summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>The vault's TOTP secret: pending while <see cref="TotpEnabled"/> is false (enrolment started), confirmed once it is true. Null if none.</summary>
    public SecretId? TotpSecretId { get; set; }

    /// <summary>Set when the person confirmed their authenticator: logins then need a second step.</summary>
    public bool TotpEnabled { get; set; }

    /// <summary>Consecutive failed attempts since the last success or lockout.</summary>
    public int FailedAttempts { get; set; }

    /// <summary>While in the future, logins are refused whatever is presented.</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    public bool Disabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A role a user holds. Global scope; per-tag scopes are not modelled.</summary>
internal sealed class RoleAssignmentRow
{
    public UserId UserId { get; set; }

    public Role Role { get; set; }
}

/// <summary>
/// An API token. Only <see cref="SecretHash"/> (SHA-256 of the 256-bit secret) is stored: a fast hash is enough for a
/// high-entropy secret, and the lookup is by <see cref="Id"/>, which is in the token string.
/// </summary>
internal sealed class ApiTokenRow
{
    public ApiTokenId Id { get; set; }

    public UserId UserId { get; set; }

    public string Name { get; set; } = "";

    /// <summary>The scopes as permission names (<see cref="ModelNames.ToName(Permission)"/>), comma-separated, in a fixed order.</summary>
    public string Scopes { get; set; } = "";

    public byte[] SecretHash { get; set; } = [];

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Updated at most once per throttle interval, so authenticating is not a write per request.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// An admin session. The browser holds a random 256-bit value; only its SHA-256 (<see cref="TokenHash"/>) is stored, so
/// a copy of the database yields no usable cookie.
/// </summary>
internal sealed class SessionRow
{
    public SessionId Id { get; set; }

    public byte[] TokenHash { get; set; } = [];

    public UserId UserId { get; set; }

    public string? ClientIp { get; set; }

    public string? UserAgent { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>The absolute end of the session; the idle limit is applied on top of it from <see cref="LastSeenAt"/>.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>False for a partial session: the password was right but the user still owes the second factor.</summary>
    public bool SecondFactorCompleted { get; set; }
}

/// <summary>A single-use recovery code for a user with TOTP; only its SHA-256 is stored.</summary>
internal sealed class RecoveryCodeRow
{
    public UserId UserId { get; set; }

    public byte[] CodeHash { get; set; } = [];

    public DateTimeOffset? UsedAt { get; set; }
}

internal sealed class IdentityConfiguration :
    IEntityTypeConfiguration<UserRow>,
    IEntityTypeConfiguration<RoleAssignmentRow>,
    IEntityTypeConfiguration<ApiTokenRow>,
    IEntityTypeConfiguration<SessionRow>,
    IEntityTypeConfiguration<RecoveryCodeRow>
{
    public const int MaxUsernameLength = 64;

    public const int MaxDisplayNameLength = 128;

    public const int MaxTokenNameLength = 128;

    public const int MaxUserAgentLength = 512;

    public void Configure(EntityTypeBuilder<UserRow> builder)
    {
        builder.ToTable("Users", table =>
            table.HasCheckConstraint("CK_Users_Totp", "\"TotpEnabled\" = 0 OR \"TotpSecretId\" IS NOT NULL"));
        builder.Property(u => u.Username).HasMaxLength(MaxUsernameLength).UseCollation("NOCASE");
        builder.Property(u => u.DisplayName).HasMaxLength(MaxDisplayNameLength);
        builder.HasIndex(u => u.Username).IsUnique();
    }

    public void Configure(EntityTypeBuilder<RoleAssignmentRow> builder)
    {
        builder.ToTable("RoleAssignments", table => table.HasCheckConstraint("CK_RoleAssignments_Role", NameChecks.Role("Role")));
        builder.HasKey(r => new { r.UserId, r.Role });
        builder.HasOne<UserRow>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<ApiTokenRow> builder)
    {
        builder.ToTable("ApiTokens", table =>
        {
            table.HasCheckConstraint("CK_ApiTokens_SecretHash", "length(\"SecretHash\") = 32");
            table.HasCheckConstraint("CK_ApiTokens_Scopes", "length(\"Scopes\") > 0");
        });
        builder.Property(t => t.Name).HasMaxLength(MaxTokenNameLength);
        builder.HasIndex(t => t.UserId);
        builder.HasOne<UserRow>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<SessionRow> builder)
    {
        builder.ToTable("Sessions", table => table.HasCheckConstraint("CK_Sessions_TokenHash", "length(\"TokenHash\") = 32"));
        builder.Property(s => s.UserAgent).HasMaxLength(MaxUserAgentLength);
        builder.Property(s => s.ClientIp).HasMaxLength(64);
        builder.HasIndex(s => s.TokenHash).IsUnique();
        builder.HasIndex(s => s.UserId);
        builder.HasIndex(s => s.ExpiresAt);
        builder.HasOne<UserRow>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<RecoveryCodeRow> builder)
    {
        builder.ToTable("RecoveryCodes", table => table.HasCheckConstraint("CK_RecoveryCodes_CodeHash", "length(\"CodeHash\") = 32"));
        builder.HasKey(r => new { r.UserId, r.CodeHash });
        builder.HasOne<UserRow>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
