using Paduraksa.Contracts.Security;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Pages;

/// <summary>What the security page and the security statistics show, worked out from the overview.</summary>
internal static class SecurityFigures
{
    /// <summary>A reason the gate refused requests, as the chart and the summary line name and colour it.</summary>
    public sealed record RefusalKind(string Name, string Key, string Css, Func<RefusalReasons, long> Of);

    /// <summary>The refusal reasons in the order the chart stacks them, the unknown reason (minutes stored before the reasons were kept) last.</summary>
    public static IReadOnlyList<RefusalKind> RefusalKinds { get; } =
    [
        new("Rate limit", "rate-limit", "pdk-chart-r1", static reasons => reasons.RateLimit),
        new("Access list", "access-list", "pdk-chart-r2", static reasons => reasons.AccessList),
        new("Rules", "rules", "pdk-chart-r3", static reasons => reasons.RuleSet),
        new("Proof of work", "proof-of-work", "pdk-chart-r4", static reasons => reasons.ProofOfWork),
        new("Sign-in", "sign-in", "pdk-chart-r5", static reasons => reasons.SignIn),
        new("Request limits", "request-limits", "pdk-chart-r6", static reasons => reasons.RequestLimits),
        new("Connection", "connection", "pdk-chart-r7", static reasons => reasons.Connection),
        new("Other", "other", "pdk-chart-r8", static reasons => reasons.Other),
        new("Unknown reason", "unknown", "pdk-chart-r9", static reasons => reasons.Unknown),
    ];

    // The numbers are DisplayFormat's. These keep the names the pages and other branches call; they write the invariant default, and a page
    // that shows them to a person calls the injected DisplayFormat instead.
    public static string RateText(double? rate) => DisplayFormat.Invariant.RateText(rate);

    public static string Count(long value) => DisplayFormat.Invariant.Count(value);

    public static string? Share(long part, long whole) => DisplayFormat.Invariant.ShareOrNull(part, whole);

    /// <summary>"rate limit 900 · access list 250": the reasons that refused anything, most first.</summary>
    public static string ReasonsText(RefusalReasons reasons, DisplayFormat? format = null) => string.Join(
        " · ",
        RefusalKinds.Select(kind => (kind, count: kind.Of(reasons))).Where(static pair => pair.count > 0).OrderByDescending(static pair => pair.count)
            .Select(pair => $"{pair.kind.Name.ToLowerInvariant()} {(format ?? DisplayFormat.Invariant).Count(pair.count)}"));

    /// <summary>"256", or "Off" for the limit that is turned off.</summary>
    public static string LimitText(int limit, DisplayFormat? format = null) => limit == 0 ? "Off" : (format ?? DisplayFormat.Invariant).Count(limit);

    public static bool HasProtection(HostProtections host) =>
        host.ProofOfWork.Enabled || host.ProofOfWork.RouteOverrides > 0
        || host.RateLimit.Policy is not null || host.RateLimit.Overrides.Count > 0
        || host.Robots != RobotsMode.Passthrough || host.AccessList.List is not null || host.AccessList.Overrides.Count > 0
        || host.RuleSet is { Set: not null } || host.RuleSet is { Overrides.Count: > 0 }
        || host.AuthProvider is { Provider: not null } || host.AuthProvider is { Overrides.Count: > 0 }
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

    /// <summary>What a set has refused, in a few words: "1,204 refused", or nothing yet. A drop is a refusal too.</summary>
    public static string? RefusedText(RuleSetRef set, DisplayFormat? format = null) =>
        set.Denied + set.Dropped is var refused and > 0 ? $"{(format ?? DisplayFormat.Invariant).Count(refused)} refused" : null;

    /// <summary>A set's regex matches that timed out, said only once there are some: it should never happen, so it is worth a line.</summary>
    public static string? TimeoutText(RuleSetRef set, DisplayFormat? format = null) =>
        set.Timeouts > 0 ? $"{(format ?? DisplayFormat.Invariant).Count(set.Timeouts)} regex {(set.Timeouts == 1 ? "timeout" : "timeouts")}" : null;

    /// <summary>What a provider has decided, in a few words: "1,204 allowed, 12 refused", or that it hasn't been asked yet.</summary>
    public static string DecidedText(AuthProviderRef provider, DisplayFormat? format = null)
    {
        format ??= DisplayFormat.Invariant;
        return provider.Allowed + provider.Refused == 0
            ? "not asked yet"
            : provider.Refused == 0 ? $"{format.Count(provider.Allowed)} allowed" : $"{format.Count(provider.Allowed)} allowed, {format.Count(provider.Refused)} refused";
    }

    /// <summary>A provider's failed calls, said only once there are some: they refuse requests, so they are worth a line.</summary>
    public static string? FailureText(AuthProviderRef provider, DisplayFormat? format = null) =>
        provider.Failures > 0 ? $"{(format ?? DisplayFormat.Invariant).Count(provider.Failures)} failed {(provider.Failures == 1 ? "call" : "calls")}" : null;

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
