using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Paduraksa.Core.Model;

namespace Paduraksa.Data.Honeypot;

/// <summary>Where the recording cuts what a client sent, so one request can't make a row of any size.</summary>
internal static class HoneypotRowLimits
{
    public const int UserAgentLength = 512;
    public const int CanaryLength = 64;
    public const int AcceptLength = 256;
    public const int AcceptLanguageLength = 128;
    public const int SecFetchLength = 32;
    public const int RefererLength = 512;
    public const int ProtocolLength = 16;
    public const int PathLength = 512;
}

/// <summary>
/// One request the honeypot answered, as the gate reported it (docs/design/waf.md "Honeypot", "Recording"). The kinds are the numbers of the
/// proxy's enums (<c>HoneypotEntry</c>, <c>HoneypotPageKind</c>, <c>HoneypotLinkKind</c>, <c>HoneypotPowOutcome</c>), which the application layer names.
/// Int64Ordered ID: high-volume, append-only and admin-only.
/// </summary>
internal sealed class HoneypotRequestRow
{
    public HoneypotRequestId Id { get; set; }

    public HoneypotSessionId SessionId { get; set; }

    /// <summary>When the request arrived, as Unix milliseconds.</summary>
    public long Time { get; set; }

    public int Entry { get; set; }

    public int PageKind { get; set; }

    public int LinkKind { get; set; }

    /// <summary>The page's ID, its kind in the bits above the 28 of the ID itself; named a key because it is no typed ID.</summary>
    public long PageKey { get; set; }

    public long ParentKey { get; set; }

    public int Status { get; set; }

    public int Pow { get; set; }

    /// <summary>With a solved challenge, the seconds from the challenge's issue to its verification, in milliseconds.</summary>
    public int SolveMilliseconds { get; set; }

    public string Protocol { get; set; } = "";

    /// <summary>The negotiated TLS version as <c>System.Security.Authentication.SslProtocols</c> numbers it; 0 on plain HTTP.</summary>
    public int TlsVersion { get; set; }

    public string? Accept { get; set; }

    public string? AcceptLanguage { get; set; }

    public string? SecFetchSite { get; set; }

    public string? SecFetchMode { get; set; }

    public string? SecFetchDest { get; set; }

    public string? SecFetchUser { get; set; }

    public string? Referer { get; set; }

    /// <summary>The canonical path the gate matched on, without the query string; the trap's verify path for a verify request, and the probed path for a rule's landing page.</summary>
    public string Path { get; set; } = "";

    /// <summary>The path was longer than <see cref="HoneypotRowLimits.PathLength"/> and <see cref="Path"/> is its beginning.</summary>
    public bool PathTruncated { get; set; }
}

internal sealed class HoneypotRequestRowConfiguration : IEntityTypeConfiguration<HoneypotRequestRow>
{
    public void Configure(EntityTypeBuilder<HoneypotRequestRow> builder)
    {
        builder.ToTable("HoneypotRequests");
        builder.Property(row => row.Protocol).HasMaxLength(HoneypotRowLimits.ProtocolLength);
        builder.Property(row => row.Accept).HasMaxLength(HoneypotRowLimits.AcceptLength);
        builder.Property(row => row.AcceptLanguage).HasMaxLength(HoneypotRowLimits.AcceptLanguageLength);
        builder.Property(row => row.SecFetchSite).HasMaxLength(HoneypotRowLimits.SecFetchLength);
        builder.Property(row => row.SecFetchMode).HasMaxLength(HoneypotRowLimits.SecFetchLength);
        builder.Property(row => row.SecFetchDest).HasMaxLength(HoneypotRowLimits.SecFetchLength);
        builder.Property(row => row.SecFetchUser).HasMaxLength(HoneypotRowLimits.SecFetchLength);
        builder.Property(row => row.Referer).HasMaxLength(HoneypotRowLimits.RefererLength);
        builder.Property(row => row.Path).HasMaxLength(HoneypotRowLimits.PathLength);

        // A session's requests in order, and the cap's oldest-first delete by the session it removes.
        builder.HasIndex(row => new { row.SessionId, row.Id });
        // Retention by age.
        builder.HasIndex(row => row.Time);
    }
}
