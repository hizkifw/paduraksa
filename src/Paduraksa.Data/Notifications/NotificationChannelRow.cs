using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paduraksa.Core.Model;
using Paduraksa.Data.Conversion;

namespace Paduraksa.Data.Notifications;

/// <summary>
/// A notification channel (docs/design/config.md "Data model"). An operational table, like DNS providers: it holds state outside the config document (when it last
/// sent, what failed) and secrets that must never be in one. <b>No secret here:</b> the URL, token or password is a vault secret.
/// </summary>
internal sealed class NotificationChannelRow
{
    public NotificationChannelId Id { get; set; }

    public NotificationChannelKind Kind { get; set; }

    public string Label { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>The subscribed events' stable names (<see cref="ModelNames.ToName(NotificationEventKind)"/>), as a JSON array.</summary>
    public List<string> Events { get; set; } = [];

    /// <summary>The server's base URL (ntfy, Gotify); not secret.</summary>
    public string? ServerUrl { get; set; }

    public string? Topic { get; set; }

    public string? SmtpHost { get; set; }

    public int? SmtpPort { get; set; }

    public SmtpSecurity? SmtpSecurity { get; set; }

    public string? SmtpUsername { get; set; }

    public string? MailFrom { get; set; }

    public List<string> MailTo { get; set; } = [];

    /// <summary>The vault secret the channel is reached with (<c>SecretKind.NotificationSecret</c>): the webhook URL, a token or the SMTP password. Null when the kind needs none and none was given.</summary>
    public SecretId? SecretId { get; set; }

    /// <summary>A webhook's signing key (<c>SecretKind.HmacSigningKey</c>); null when bodies are not signed.</summary>
    public SecretId? SigningSecretId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? LastSentAt { get; set; }

    public DateTimeOffset? LastErrorAt { get; set; }

    /// <summary>What the last failed delivery said, as one short sanitised line; cleared by the next success.</summary>
    public string? LastError { get; set; }
}

internal sealed class NotificationChannelRowConfiguration : IEntityTypeConfiguration<NotificationChannelRow>
{
    public void Configure(EntityTypeBuilder<NotificationChannelRow> builder)
    {
        builder.ToTable("NotificationChannels", table =>
        {
            table.HasCheckConstraint("CK_NotificationChannels_Kind", NameChecks.NotificationChannelKind("Kind"));
            table.HasCheckConstraint("CK_NotificationChannels_SmtpSecurity", $"\"SmtpSecurity\" IS NULL OR {NameChecks.SmtpSecurity("SmtpSecurity")}");
        });
    }
}

/// <summary>
/// A certificate-expiry notice that was sent: <b>one row per certificate, chain and threshold</b>, so a restart does not send it again and a
/// renewal (a new <c>NotAfter</c>) starts the thresholds over. Deleted with its certificate.
/// </summary>
internal sealed class ExpiryNoticeRow
{
    public CertificateId CertificateId { get; set; }

    /// <summary>The chain's expiry, whole Unix seconds: which issue of the certificate this notice was about.</summary>
    public long NotAfter { get; set; }

    /// <summary>The threshold crossed, in days (30, 14, 7, 1).</summary>
    public int Days { get; set; }

    public DateTimeOffset SentAt { get; set; }
}

internal sealed class ExpiryNoticeRowConfiguration : IEntityTypeConfiguration<ExpiryNoticeRow>
{
    public void Configure(EntityTypeBuilder<ExpiryNoticeRow> builder)
    {
        builder.ToTable("ExpiryNotices");
        builder.HasKey(n => new { n.CertificateId, n.NotAfter, n.Days });
        builder.HasOne<Paduraksa.Data.Certificates.CertificateRow>().WithMany().HasForeignKey(n => n.CertificateId).OnDelete(DeleteBehavior.Cascade);
    }
}
