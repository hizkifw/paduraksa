using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paduraksa.Core.Model;
using Paduraksa.Data.Conversion;

namespace Paduraksa.Data.Certificates;

/// <summary>
/// A certificate's metadata: an operational resource, not part of the config document, so it has no revision and is
/// audited one by one. The vault holds the key and the chain it serves; this row holds the public facts about them, so
/// the UI can list certificates, the compiler can match them to host names and the scheduler can find the ones that
/// need renewing, all without the vault. <b>Public data only: there is no private key here, and no column could hold
/// one.</b> Host bindings reference a certificate by ID without a foreign key, because the document that names it is a
/// revision, not a row; the application refuses to delete a certificate a binding uses.
/// </summary>
internal sealed class CertificateRow
{
    public CertificateId Id { get; set; }

    public CertificateSource Source { get; set; }

    public CertificateStatus Status { get; set; }

    /// <summary>Why the certificate is <see cref="CertificateStatus.Failed"/> (an error summary, no secrets); otherwise null.</summary>
    public string? StatusMessage { get; set; }

    /// <summary>The subject DN (RFC 4514), or empty.</summary>
    public string Subject { get; set; } = "";

    public string Issuer { get; set; } = "";

    /// <summary>The serial number as the DER INTEGER content bytes, uppercase hex.</summary>
    public string SerialNumber { get; set; } = "";

    /// <summary>The Authority Key Identifier's key identifier as lowercase hex, or null.</summary>
    public string? AuthorityKeyIdentifier { get; set; }

    /// <summary>SHA-256 of the leaf's DER, lowercase hex.</summary>
    public string Sha256Fingerprint { get; set; } = "";

    /// <summary>A display label for the public key, e.g. <c>ECDSA P-256</c>.</summary>
    public string KeyAlgorithm { get; set; } = "";

    public DateTimeOffset NotBefore { get; set; }

    public DateTimeOffset NotAfter { get; set; }

    /// <summary>The issuer DN of every certificate in the chain, in order, as a JSON array of strings.</summary>
    public string ChainIssuers { get; set; } = "[]";

    /// <summary>The leaf and its intermediates as PEM <c>CERTIFICATE</c> blocks. Public.</summary>
    public string PublicChainPem { get; set; } = "";

    public string? Label { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One name a certificate covers (a SAN): a DNS name, a <c>*.parent</c> wildcard or an IP address, stored normalised so
/// "which certificates cover this name" is an indexed lookup: the exact name, or the wildcard of its parent. Deleted
/// with the certificate.
/// </summary>
internal sealed class CertificateNameRow
{
    public CertificateId CertificateId { get; set; }

    /// <summary>Normalised like host names (lowercase, punycode, no trailing dot); IP addresses in canonical text.</summary>
    public string Name { get; set; } = "";
}

internal sealed class CertificateRowConfiguration : IEntityTypeConfiguration<CertificateRow>, IEntityTypeConfiguration<CertificateNameRow>
{
    public void Configure(EntityTypeBuilder<CertificateRow> builder)
    {
        builder.ToTable("Certificates", table =>
        {
            table.HasCheckConstraint("CK_Certificates_Source", NameChecks.CertificateSource("Source"));
            table.HasCheckConstraint("CK_Certificates_Status", NameChecks.CertificateStatus("Status"));
        });
        builder.HasIndex(c => c.Sha256Fingerprint);
        builder.HasIndex(c => c.NotAfter);
    }

    public void Configure(EntityTypeBuilder<CertificateNameRow> builder)
    {
        builder.ToTable("CertificateNames");
        builder.HasKey(n => new { n.CertificateId, n.Name });
        builder.Property(n => n.Name).UseCollation("NOCASE");
        builder.HasIndex(n => n.Name);
        builder.HasOne<CertificateRow>().WithMany().HasForeignKey(n => n.CertificateId).OnDelete(DeleteBehavior.Cascade);
    }
}
