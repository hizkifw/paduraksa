using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Paduraksa.Core.Model;

namespace Paduraksa.Data.Honeypot;

/// <summary>
/// One visitor's session in a host's honeypot (docs/design/waf.md "Honeypot", "Recording"): the host, the client address in full and the user agent, from its
/// first request until it had been idle for 30 minutes. The counters are the session's running totals, so a write replaces them. A session's
/// requests are <see cref="HoneypotRequestRow"/>s. Int64Ordered ID: high-volume, admin-only, and ordered by when the session began.
/// </summary>
internal sealed class HoneypotSessionRow
{
    public HoneypotSessionId Id { get; set; }

    /// <summary>The host's typed-ID text (<c>host_...</c>), kept after the host is deleted.</summary>
    public string HostKey { get; set; } = "";

    /// <summary>The client address in full, as text (an IPv4 client is plain dotted form).</summary>
    public string ClientIp { get; set; } = "";

    /// <summary>The <c>User-Agent</c>, cut to a fixed length; empty when the client sent none.</summary>
    public string UserAgent { get; set; } = "";

    /// <summary>The visitor's canary phrase.</summary>
    public string Canary { get; set; } = "";

    /// <summary>The first request, as Unix milliseconds.</summary>
    public long StartedAt { get; set; }

    /// <summary>The latest request, as Unix milliseconds.</summary>
    public long LastSeen { get; set; }

    /// <summary>The visitor fetched robots.txt before the session's first request.</summary>
    public bool ReadRobotsFirst { get; set; }

    public int Requests { get; set; }

    /// <summary>Requests for a page of the site (index, post, category, tag, author, pagination, a landing), the challenge's verify request not counted.</summary>
    public int Pages { get; set; }

    /// <summary>Requests for a URL that is no page.</summary>
    public int GuessedUrls { get; set; }

    public int Images { get; set; }

    /// <summary>Requests only a client that runs the page's script makes: the beacon and the script link.</summary>
    public int JsRuns { get; set; }

    public int ChallengesShown { get; set; }

    public int ChallengesSolved { get; set; }

    public int ChallengesPassed { get; set; }

    /// <summary>The most links followed from the index to reach a page of this session.</summary>
    public int MaxDepth { get; set; }

    // One counter per link kind, in the order of HoneypotLinkKind. A column each, so the summary asks the table how many sessions followed one.
    public int LinkDirect { get; set; }

    public int LinkNormal { get; set; }

    public int LinkNoFollow { get; set; }

    public int LinkUgc { get; set; }

    public int LinkSponsored { get; set; }

    public int LinkHidden { get; set; }

    public int LinkComment { get; set; }

    public int LinkScript { get; set; }

    public int LinkBeacon { get; set; }

    public int LinkSitemap { get; set; }

    public int LinkFeed { get; set; }

    public int LinkNoIndexPage { get; set; }

    public int LinkImage { get; set; }
}

internal sealed class HoneypotSessionRowConfiguration : IEntityTypeConfiguration<HoneypotSessionRow>
{
    public void Configure(EntityTypeBuilder<HoneypotSessionRow> builder)
    {
        builder.ToTable("HoneypotSessions");
        builder.Property(row => row.HostKey).HasMaxLength(64);
        builder.Property(row => row.ClientIp).HasMaxLength(45);
        builder.Property(row => row.UserAgent).HasMaxLength(HoneypotLimits.RecordedUserAgentLength);
        builder.Property(row => row.Canary).HasMaxLength(HoneypotLimits.RecordedCanaryLength);

        // The range filter, the list's newest-first walk after it and retention all walk sessions by their last request.
        builder.HasIndex(row => row.LastSeen);
    }
}
