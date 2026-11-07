using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Vault.Persistence;

/// <summary>
/// The one row that says which KEK this database was written under (<see cref="Crypto.Kek.CheckValue"/>). Startup compares
/// it with the KEK it was given, so a wrong KEK stops the process there instead of failing at the first secret.
/// </summary>
internal sealed class VaultMetaRow
{
    /// <summary>Always <see cref="SingletonSlot"/>: there is one row.</summary>
    public int Slot { get; set; } = SingletonSlot;

    public byte[] KekCheck { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the KEK was last rotated, or the creation time if never.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    public const int SingletonSlot = 1;
}

/// <summary>
/// A secret (PLAN §8.2). <see cref="Sealed"/> holds the value as <see cref="Crypto.EnvelopeCipher"/> made it, bound to
/// <c>(Id, Kind, Version)</c>. A MAC key also keeps the version it replaced, for the verify grace window.
/// </summary>
internal sealed class SecretRow
{
    public SecretId Id { get; set; }

    public SecretKind Kind { get; set; }

    public string? Label { get; set; }

    /// <summary>Set exactly for the MAC kinds; fixed when the secret is created.</summary>
    public MacAlgorithm? MacAlgorithm { get; set; }

    /// <summary>Normalised hosts (<see cref="VaultHostNames"/>); non-empty exactly for <see cref="SecretKind.DnsApiToken"/>.</summary>
    public List<string> AllowedHosts { get; set; } = [];

    public int Version { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public byte[] Sealed { get; set; } = [];

    /// <summary>The sealed value of <see cref="PreviousVersion"/>, kept until <see cref="PreviousRetiredAt"/> plus the grace window; null if none.</summary>
    public byte[]? PreviousSealed { get; set; }

    public int? PreviousVersion { get; set; }

    public DateTimeOffset? PreviousRetiredAt { get; set; }
}

/// <summary>An ACME account key: the private key sealed, the public parts and thumbprint in the clear (they are public).</summary>
internal sealed class AccountKeyRow
{
    public AcmeAccountId Id { get; set; }

    public JwsAlgorithm Algorithm { get; set; }

    /// <summary>JWK <c>kty</c>: <c>EC</c> or <c>RSA</c>.</summary>
    public string Kty { get; set; } = "";

    public string? Crv { get; set; }

    public string? X { get; set; }

    public string? Y { get; set; }

    public string? N { get; set; }

    public string? E { get; set; }

    /// <summary>RFC 7638 SHA-256 thumbprint, base64url.</summary>
    public string Thumbprint { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The PKCS#8 key as <see cref="Crypto.EnvelopeCipher"/> made it, at the fixed <see cref="SealedVersion"/>.</summary>
    public byte[] Sealed { get; set; } = [];

    /// <summary>An account key is never replaced (rollover is deferred, PLAN §9), so its seal version is always 1.</summary>
    public const int SealedVersion = 1;
}

internal sealed class VaultMetaRowConfiguration : IEntityTypeConfiguration<VaultMetaRow>
{
    public void Configure(EntityTypeBuilder<VaultMetaRow> builder)
    {
        builder.ToTable("VaultMeta", table =>
        {
            table.HasCheckConstraint("CK_VaultMeta_Singleton", $"\"Slot\" = {VaultMetaRow.SingletonSlot}");
            table.HasCheckConstraint("CK_VaultMeta_KekCheck", "length(\"KekCheck\") = 32");
        });
        builder.HasKey(r => r.Slot);
        builder.Property(r => r.Slot).ValueGeneratedNever();
    }
}

internal sealed class SecretRowConfiguration : IEntityTypeConfiguration<SecretRow>
{
    public void Configure(EntityTypeBuilder<SecretRow> builder)
    {
        builder.ToTable("Secrets", table =>
        {
            table.HasCheckConstraint("CK_Secrets_Kind", NameChecks.SecretKind("Kind"));
            table.HasCheckConstraint("CK_Secrets_MacAlgorithm", $"\"MacAlgorithm\" IS NULL OR {NameChecks.MacAlgorithm("MacAlgorithm")}");
            table.HasCheckConstraint("CK_Secrets_MacKinds", $"(\"MacAlgorithm\" IS NOT NULL) = ({NameChecks.MacKinds("Kind")})");
            table.HasCheckConstraint("CK_Secrets_Version", "\"Version\" >= 1");
            table.HasCheckConstraint("CK_Secrets_Sealed", "length(\"Sealed\") > 0");
            table.HasCheckConstraint(
                "CK_Secrets_Previous",
                "(\"PreviousSealed\" IS NULL) = (\"PreviousVersion\" IS NULL) AND (\"PreviousSealed\" IS NULL) = (\"PreviousRetiredAt\" IS NULL)");
        });
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Label).HasMaxLength(256);
        builder.Property(r => r.Sealed).IsRequired();

        // The version is in the AAD of the sealed value, so a write that lost a race with another process (a CLI replacing
        // the same secret) must fail rather than seal a value under a version someone else just used.
        builder.Property(r => r.Version).IsConcurrencyToken();
    }
}

internal sealed class AccountKeyRowConfiguration : IEntityTypeConfiguration<AccountKeyRow>
{
    public void Configure(EntityTypeBuilder<AccountKeyRow> builder)
    {
        builder.ToTable("AccountKeys", table =>
        {
            table.HasCheckConstraint("CK_AccountKeys_Algorithm", NameChecks.JwsAlgorithm("Algorithm"));
            table.HasCheckConstraint("CK_AccountKeys_Kty", "\"Kty\" IN ('EC', 'RSA')");
            table.HasCheckConstraint("CK_AccountKeys_Sealed", "length(\"Sealed\") > 0");
        });
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Kty).HasMaxLength(8);
        builder.Property(r => r.Crv).HasMaxLength(8);
        builder.Property(r => r.Thumbprint).HasMaxLength(64);
        builder.Property(r => r.Sealed).IsRequired();
    }
}
