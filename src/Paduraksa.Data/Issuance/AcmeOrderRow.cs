using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Data.Certificates;
using Paduraksa.Data.Conversion;

namespace Paduraksa.Data.Issuance;

/// <summary>
/// The ACME issuance state of one certificate: <b>one row per certificate</b> (unique <see cref="CertificateId"/>),
/// reused by every issuance and renewal of it, so "one issuance per certificate at a time" is a property of the table.
/// It carries what an attempt needs to resume after a restart (the order URL, the staged key's token and the CSR), what
/// the last failure was, and the renewal schedule; the certificate's serving metadata stays on its
/// <see cref="CertificateRow"/>. <b>Public data only:</b> the staged key is a token, the CSR is a public request.
/// </summary>
internal sealed class AcmeOrderRow
{
    public AcmeOrderId Id { get; set; }

    public CertificateId CertificateId { get; set; }

    public AcmeAccountId AccountId { get; set; }

    public AcmeOrderStatus Status { get; set; }

    public AcmeChallengeType ChallengeType { get; set; }

    /// <summary>The DNS provider for <see cref="AcmeChallengeType.Dns01"/>; null for HTTP-01.</summary>
    public DnsProviderId? DnsProviderId { get; set; }

    public KeySpec KeySpec { get; set; }

    /// <summary>The names the certificate is for, normalised (a JSON array column).</summary>
    public List<string> Names { get; set; } = [];

    /// <summary>The CA's ACME profile name (for example <c>shortlived</c>), or null for the CA's default.</summary>
    public string? Profile { get; set; }

    /// <summary>The issuer common name the chain should end in (<c>preferred_chain</c>), or null for the CA's default chain.</summary>
    public string? PreferredChain { get; set; }

    /// <summary>The order URL at the CA; null until <c>newOrder</c> succeeded.</summary>
    public string? OrderUrl { get; set; }

    /// <summary>The key the vault staged for this attempt; attached with the chain. Null before the CSR step.</summary>
    public StagedKeyId? StagedKeyId { get; set; }

    /// <summary>The PKCS#10 request (DER) the vault made for <see cref="StagedKeyId"/>; sent again when a restart
    /// caught the order before the CA accepted it.</summary>
    public byte[]? Csr { get; set; }

    /// <summary>
    /// The vault's public <c>CertificateInfo</c> (JSON) of the chain it attached for this order, saved when the attach
    /// returned and cleared when the certificate is registered. A restart that finds it registers from it instead of
    /// attaching again, the staged key being spent by then. Public data only.
    /// </summary>
    public string? AttachedInfo { get; set; }

    /// <summary>What the last failed attempt said (CA text is sanitised and cut); null when the last attempt did not fail.</summary>
    public string? Error { get; set; }

    /// <summary>Failed attempts in a row since the last success. Drives the backoff.</summary>
    public int Attempts { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>After a failure, the earliest time the scheduler tries again (backoff, or the CA's <c>Retry-After</c> if later). Null when there is none.</summary>
    public DateTimeOffset? RetryAt { get; set; }

    /// <summary>When the scheduler starts the next renewal (ARI's pick, else a third of the lifetime before expiry). Null until the certificate is issued.</summary>
    public DateTimeOffset? NextRenewalAt { get; set; }

    /// <summary>The CA's suggested renewal window (ARI, RFC 9773), when it offered one.</summary>
    public DateTimeOffset? AriWindowStart { get; set; }

    public DateTimeOffset? AriWindowEnd { get; set; }

    /// <summary>A page the CA gave to explain an unusual window, to show to the operator.</summary>
    public string? AriExplanationUrl { get; set; }

    /// <summary>When ARI is asked again (the response's <c>Retry-After</c>, bounded).</summary>
    public DateTimeOffset? AriCheckAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class AcmeOrderRowConfiguration : IEntityTypeConfiguration<AcmeOrderRow>
{
    public void Configure(EntityTypeBuilder<AcmeOrderRow> builder)
    {
        builder.ToTable("AcmeOrders", table =>
        {
            table.HasCheckConstraint("CK_AcmeOrders_Status", NameChecks.AcmeOrderStatus("Status"));
            table.HasCheckConstraint("CK_AcmeOrders_ChallengeType", NameChecks.AcmeChallengeType("ChallengeType"));
            table.HasCheckConstraint("CK_AcmeOrders_KeySpec", NameChecks.KeySpec("KeySpec"));
            table.HasCheckConstraint("CK_AcmeOrders_DnsProvider", $"\"ChallengeType\" <> '{AcmeChallengeType.Dns01.ToName()}' OR \"DnsProviderId\" IS NOT NULL");
        });
        builder.HasIndex(o => o.CertificateId).IsUnique();
        builder.HasOne<CertificateRow>().WithMany().HasForeignKey(o => o.CertificateId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AcmeAccountRow>().WithMany().HasForeignKey(o => o.AccountId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DnsProviderRow>().WithMany().HasForeignKey(o => o.DnsProviderId).OnDelete(DeleteBehavior.Restrict);
    }
}
