using System.Globalization;

using Paduraksa.Contracts.Security;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Pages;

/// <summary>What the security page shows, worked out from the overview and the sample before it.</summary>
internal static class SecurityFigures
{
    /// <summary>Everything the proof-of-work gate refused: bad solutions, non-browsers and each kind of bad cookie.</summary>
    public static long Rejected(ProofOfWorkCounters pow) =>
        pow.VerifyRejected + pow.BlockedNonBrowser + pow.TokensRejected.Malformed + pow.TokensRejected.BadTag + pow.TokensRejected.Expired + pow.TokensRejected.TooEasy;

    /// <summary>Per-second change between two samples; null when there is no earlier sample or no time passed between them.</summary>
    public static double? Rate(long before, long after, TimeSpan elapsed) =>
        elapsed > TimeSpan.Zero ? Math.Max(0, after - before) / elapsed.TotalSeconds : null;

    /// <summary>"0.4/s", "12/s", "0/s"; blank before a rate exists, so the tile keeps its height.</summary>
    public static string RateText(double? rate) => rate switch
    {
        null => string.Empty,
        0 => "0/s",
        < 0.1 => "<0.1/s",
        < 10 => string.Create(CultureInfo.InvariantCulture, $"{rate:0.#}/s"),
        { } value => string.Create(CultureInfo.InvariantCulture, $"{value:0}/s"),
    };

    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"81%" of <paramref name="whole"/>; null when there is nothing to take a share of.</summary>
    public static string? Share(long part, long whole) =>
        whole <= 0 ? null : string.Create(CultureInfo.InvariantCulture, $"{Math.Min(100, part * 100.0 / whole):0}%");

    public static bool HasProtection(HostProtections host) =>
        host.ProofOfWork.Enabled || host.ProofOfWork.RouteOverrides > 0
        || host.RateLimit.Policy is not null || host.RateLimit.Overrides.Count > 0
        || host.Robots != RobotsMode.Passthrough || host.AccessList.List is not null || host.AccessList.Overrides.Count > 0
        || host.OriginLockdown != OriginLockdownMode.Off || host.AuthenticatedOriginPulls;

    /// <summary>The hosts with something on first, then those with nothing, each by name.</summary>
    public static IReadOnlyList<HostProtections> Ordered(IReadOnlyList<HostProtections> hosts) =>
    [
        .. hosts.OrderBy(host => HasProtection(host) ? 0 : 1).ThenBy(host => host.Name, StringComparer.OrdinalIgnoreCase),
    ];

    public static string Routes(int count) => count == 1 ? "1 route" : $"{count} routes";

    /// <summary>What a list asks of a visitor, in a few words: its users, or that it only looks at addresses and countries.</summary>
    public static string ListText(AccessListRef list) =>
        list.Users > 0 ? (list.Users == 1 ? "1 user" : $"{list.Users} users")
        : list.AddressRanges > 0 && list.Countries > 0 ? "addresses and countries only"
        : list.Countries > 0 ? "countries only"
        : list.AddressRanges > 0 ? "addresses only"
        : "no rules";

    public static string LockdownText(OriginLockdownMode mode) => mode switch
    {
        OriginLockdownMode.Forbid => "Cloudflare only (403)",
        OriginLockdownMode.Drop => "Cloudflare only (drop)",
        _ => string.Empty,
    };

    public static string RobotsText(RobotsMode mode) => mode switch
    {
        RobotsMode.DisallowAll => "Disallow all",
        RobotsMode.BlockAiCrawlers => "Blocks AI crawlers",
        RobotsMode.Custom => "Custom",
        _ => string.Empty,
    };
}
