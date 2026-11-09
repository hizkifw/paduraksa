using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Data.Conversion;

namespace Paduraksa.Data.Issuance;

/// <summary>
/// An ACME account: which CA, who to contact, and the account URL the CA gave it. An operational resource, not part of
/// the config document. <b>No key material:</b> the account key lives in the vault under <see cref="Id"/>
/// (<c>IJwsSigner</c>), and the external-account-binding MAC key is a vault secret this row only references by
/// <see cref="EabSecretId"/>.
/// </summary>
internal sealed class AcmeAccountRow
{
    public AcmeAccountId Id { get; set; }

    /// <summary>The CA's directory URL (Let's Encrypt, its staging environment, ZeroSSL, Google, or a custom one).</summary>
    public string DirectoryUrl { get; set; } = "";

    /// <summary>Contact URIs (<c>mailto:...</c>) as a JSON array column.</summary>
    public List<string> Contacts { get; set; } = [];

    public AcmeAccountStatus Status { get; set; }

    /// <summary>The account URL the CA assigned: the <c>kid</c> of every signed request.</summary>
    public string AccountUrl { get; set; } = "";

    /// <summary>The JWS algorithm of the vault's account key.</summary>
    public JwsAlgorithm KeyAlgorithm { get; set; }

    /// <summary>The CA's external-account-binding key identifier (not secret), when the account was bound; otherwise null.</summary>
    public string? EabKid { get; set; }

    /// <summary>The vault secret that holds the binding's HMAC key, when the account was bound; otherwise null.</summary>
    public SecretId? EabSecretId { get; set; }

    public bool TermsOfServiceAgreed { get; set; }

    public string? Label { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class AcmeAccountRowConfiguration : IEntityTypeConfiguration<AcmeAccountRow>
{
    public void Configure(EntityTypeBuilder<AcmeAccountRow> builder)
    {
        builder.ToTable("AcmeAccounts", table =>
        {
            table.HasCheckConstraint("CK_AcmeAccounts_Status", NameChecks.AcmeAccountStatus("Status"));
            table.HasCheckConstraint("CK_AcmeAccounts_KeyAlgorithm", NameChecks.JwsAlgorithm("KeyAlgorithm"));
        });
        builder.HasIndex(a => a.DirectoryUrl);
    }
}
