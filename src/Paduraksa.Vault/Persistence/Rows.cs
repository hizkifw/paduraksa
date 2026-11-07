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

/// <summary>
/// A certificate the vault holds: the private key sealed, and the public chain in the clear (it is public). One row per
/// <see cref="CertificateId"/>, so a replacement (a renewal, a re-issue, a new upload) is an update of this row, and key and
/// chain change together or not at all. A row always has both: a key staged for a chain that has not been issued yet is a
/// <see cref="StagedKeyRow"/>.
/// </summary>
internal sealed class CertificateKeyRow
{
    public CertificateId Id { get; set; }

    /// <summary>True for the local CA root: the one key the vault signs with.</summary>
    public bool IsLocalCaRoot { get; set; }

    /// <summary>Bound into the AAD of <see cref="Sealed"/>; a replacement increments it, and a write that lost a race with another process fails.</summary>
    public int Version { get; set; }

    /// <summary>The leaf first, then its intermediates, as concatenated PEM <c>CERTIFICATE</c> blocks (the root only when the certificate is its own root).</summary>
    public string ChainPem { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The PKCS#8 key as <see cref="Crypto.EnvelopeCipher"/> made it, bound to <c>(Id, purpose, Version)</c>.</summary>
    public byte[] Sealed { get; set; } = [];
}

/// <summary>
/// A key generated for a CSR and waiting for its chain (<c>ICertificateIssuance.CreateKeyAndCsrAsync</c>). At most one per
/// certificate: staging again replaces it, which is what makes the earlier token stale. Sealed to its certificate, its own
/// <see cref="StagedKeyId"/> and a digest of its <see cref="Names"/>, so it cannot be moved onto another row or have its names edited.
/// </summary>
internal sealed class StagedKeyRow
{
    public CertificateId CertificateId { get; set; }

    public StagedKeyId StagedKeyId { get; set; }

    /// <summary>The names the CSR asked for; the chain that comes back must cover them.</summary>
    public List<string> Names { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public byte[] Sealed { get; set; } = [];

    /// <summary>The seal version is always 1: the row's ID and names are in the AAD, and a re-staging seals a new key under a new staged ID.</summary>
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

internal sealed class CertificateKeyRowConfiguration : IEntityTypeConfiguration<CertificateKeyRow>
{
    public void Configure(EntityTypeBuilder<CertificateKeyRow> builder)
    {
        builder.ToTable("CertificateKeys", table =>
        {
            table.HasCheckConstraint("CK_CertificateKeys_Version", "\"Version\" >= 1");
            table.HasCheckConstraint("CK_CertificateKeys_Sealed", "length(\"Sealed\") > 0");
            table.HasCheckConstraint("CK_CertificateKeys_Chain", "length(\"ChainPem\") > 0");
        });
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Sealed).IsRequired();
        builder.Property(r => r.ChainPem).IsRequired();

        // Like a secret's version, it is in the AAD: a concurrent replacement must fail rather than seal under a used version.
        builder.Property(r => r.Version).IsConcurrencyToken();

        // There is one local CA, whatever two processes do at the same time.
        builder.HasIndex(r => r.IsLocalCaRoot).IsUnique().HasFilter("\"IsLocalCaRoot\" = 1");
    }
}

internal sealed class StagedKeyRowConfiguration : IEntityTypeConfiguration<StagedKeyRow>
{
    public void Configure(EntityTypeBuilder<StagedKeyRow> builder)
    {
        builder.ToTable("StagedKeys", table => table.HasCheckConstraint("CK_StagedKeys_Sealed", "length(\"Sealed\") > 0"));
        builder.HasKey(r => r.CertificateId);
        builder.Property(r => r.Sealed).IsRequired();
        builder.HasIndex(r => r.StagedKeyId).IsUnique();
    }
}
