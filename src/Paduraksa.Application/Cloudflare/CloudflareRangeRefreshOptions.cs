namespace Paduraksa.Application.Cloudflare;

/// <summary>How the job that keeps Cloudflare's edge ranges current fetches and paces itself.</summary>
public sealed record CloudflareRangeRefreshOptions
{
    /// <summary>Whether the job fetches at all. Off, it waits until it is turned on (a server setting) and the list in force stays.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The published list as JSON (<c>result.ipv4_cidrs</c>, <c>result.ipv6_cidrs</c>).</summary>
    public Uri ApiUrl { get; init; } = new("https://api.cloudflare.com/client/v4/ips");

    /// <summary>The fallback, one IPv4 CIDR per line.</summary>
    public Uri TextV4Url { get; init; } = new("https://www.cloudflare.com/ips-v4");

    /// <summary>The fallback, one IPv6 CIDR per line.</summary>
    public Uri TextV6Url { get; init; } = new("https://www.cloudflare.com/ips-v6");

    /// <summary>How long after the process starts the first fetch waits, so startup finishes first.</summary>
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The time between two successful fetches, before <see cref="Jitter"/>.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromDays(1);

    /// <summary>Up to this much is added to every pause, so a fleet doesn't call Cloudflare in the same minute.</summary>
    public TimeSpan Jitter { get; init; } = TimeSpan.FromHours(1);

    /// <summary>The pause after the first failure; it doubles with each one in a row up to <see cref="MaxRetry"/>.</summary>
    public TimeSpan MinRetry { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan MaxRetry { get; init; } = TimeSpan.FromHours(6);

    /// <summary>One request's whole time, headers and body.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The largest response body read; Cloudflare's lists are under 2 KiB, so anything bigger is not the list.</summary>
    public int MaxBodyBytes { get; init; } = 64 * 1024;
}
