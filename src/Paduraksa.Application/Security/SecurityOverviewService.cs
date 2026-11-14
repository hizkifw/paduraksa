using System.Collections.Concurrent;

using Paduraksa.Application.Metrics;
using Paduraksa.Contracts.Metrics;
using Paduraksa.Contracts.Security;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.ForwardAuth;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Metrics;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.RateLimiting;
using Paduraksa.Proxy.RuleSets;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Application.Security;

/// <summary>
/// The security overview over a range (docs/design/waf.md "Security overview"): the gate's counters and the refusals by reason, read
/// from the metric sampler and the rollups so they outlive a restart, and the protections the live snapshot applies per host. The hour
/// is the sampler's memory and costs no store read; the longer ranges read the rollups at most once every 30 s per range.
/// </summary>
/// <remarks>
/// Protections come from the compiled snapshot rather than the stored document, so they are what the gate is doing: a revision the
/// compiler rejected never shows. A provider's or a set's counts over the range are its own series', which outlive its deletion; so do
/// the summed sign-in counters, which therefore count providers the live config no longer names.
/// </remarks>
public sealed class SecurityOverviewService(
    SnapshotStore snapshots, ConnectionRuntime connections, MetricSampler sampler, MetricsService metrics, SecurityRollupStore rollups, TimeProvider time)
{
    private static readonly TimeSpan RollupCacheFor = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<MetricsRange, CachedRead> _cache = new();

    public async Task<SecurityOverview> ReadAsync(MetricsRange range, CancellationToken cancellationToken)
    {
        var (_, step) = MetricsService.Reach(range);
        var at = time.GetUtcNow();
        var figures = await metrics.ReadRangeAsync(range, at, cancellationToken).ConfigureAwait(false);
        var security = await SecurityAsync(range, at, cancellationToken).ConfigureAwait(false);
        var total = security.Total;
        var current = snapshots.Current;
        var hosts = current.HostsByIndex
            .Select(host => HostOf(host, security) with { Refused = figures.Hosts.TryGetValue(host.Id, out var point) ? point.Refused : 0 })
            .OrderBy(host => host.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(host => host.Id.ToString(), StringComparer.Ordinal)
            .ToArray();
        var observed = figures.Series.FirstOrDefault(static point => point.CoveredSeconds > 0);
        // The hour's totals are its chart's. A longer range's are its hosts', whose last hour is the sampler's: the chart's last steps lag
        // until their minutes are written, the figures do not.
        var refused = range == MetricsRange.Hour
            ? figures.Series.Count == 0 ? default : MetricPoints.Combine([.. figures.Series], 0, 0)
            : figures.Hosts.Count == 0 ? default : MetricPoints.Combine([.. figures.Hosts.Values], 0, 0);
        return new SecurityOverview(
            range,
            at,
            new ProofOfWorkCounters(
                total[SecurityCounter.ChallengesServed], total[SecurityCounter.Solved], total[SecurityCounter.VerifyRejected], total[SecurityCounter.BlockedNonBrowser],
                new TokenRejections(total[SecurityCounter.TokenMalformed], total[SecurityCounter.TokenBadTag], total[SecurityCounter.TokenExpired], total[SecurityCounter.TokenTooEasy])),
            new RateLimitCounters(total[SecurityCounter.RateLimitAllowed], total[SecurityCounter.RateLimited]),
            new AccessListCounters(
                total[SecurityCounter.AddressRefused], total[SecurityCounter.Unauthorized], total[SecurityCounter.PasswordCacheHits], total[SecurityCounter.Verifications],
                total[SecurityCounter.Queued], total[SecurityCounter.QueueFull], total[SecurityCounter.QueueTimeouts]),
            hosts,
            new SignInCounters(
                total[SecurityCounter.SignInAllowed], total[SecurityCounter.SignInDenied], total[SecurityCounter.SignInRedirected], total[SecurityCounter.SignInCacheHits],
                total[SecurityCounter.SignInCalls], total[SecurityCounter.SignInErrors], total[SecurityCounter.SignInTimeouts], total[SecurityCounter.SignInBusy]),
            ConnectionsOf(current, total),
            ReasonsOf(refused),
            [.. figures.Series.Select(static point => new RefusalStep(DateTimeOffset.FromUnixTimeSeconds(point.Start), point.Width, point.CoveredSeconds, ReasonsOf(point)))],
            step,
            observed.Width > 0 ? Later(DateTimeOffset.FromUnixTimeSeconds(observed.Start), at - MetricsService.Reach(range).Span) : null);
    }

    // A step starts on its own grid, which can be before the range does.
    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    internal static RefusalReasons ReasonsOf(MetricPoint point)
    {
        var kinds = point.Refusals;
        return new RefusalReasons(
            kinds.AccessList, kinds.RuleSet, kinds.RateLimit, kinds.ProofOfWork, kinds.ForwardAuth, kinds.RequestLimits, kinds.Connection, kinds.Other, point.UnknownRefused,
            kinds.NoRoute, kinds.Unavailable, kinds.Dropped, kinds.UnknownHost);
    }

    private async Task<SecurityRead> SecurityAsync(MetricsRange range, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var (span, _) = MetricsService.Reach(range);
        if (range == MetricsRange.Hour)
        {
            return SecurityRead.Of(at, sampler, span);
        }

        // As the request figures: the last hour from the sampler, what is before it from the rollups (cached while the split minute holds).
        var recent = MetricsService.RecentFrom(at);
        var stored = await StoredAsync(range, at, span, recent, cancellationToken).ConfigureAwait(false);
        var memory = SecurityRead.Of(at, sampler, at - recent, recent);
        return new SecurityRead(
            at,
            SecurityPoint.Combine([stored.Total, memory.Total], 0, 0),
            Merge(stored.Providers, memory.Providers),
            Merge(stored.Sets, memory.Sets));
    }

    private static Dictionary<TKey, SecurityPoint> Merge<TKey>(IReadOnlyDictionary<TKey, SecurityPoint> stored, IReadOnlyDictionary<TKey, SecurityPoint> memory)
        where TKey : notnull
    {
        Dictionary<TKey, SecurityPoint> merged = new(stored);
        foreach (var (key, point) in memory)
        {
            merged[key] = merged.TryGetValue(key, out var other) ? SecurityPoint.Combine([other, point], 0, 0) : point;
        }

        return merged;
    }

    private async Task<SecurityRead> StoredAsync(MetricsRange range, DateTimeOffset at, TimeSpan span, DateTimeOffset recent, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(range, out var cached) && at - cached.At < RollupCacheFor && at >= cached.At && cached.Recent == recent)
        {
            return cached.Read;
        }

        var from = at - span;
        var before = recent.AddMinutes(-1);
        var totals = await rollups.SummariseAsync(SecurityRollupSubject.Total, from, before, cancellationToken).ConfigureAwait(false);
        var byProvider = await rollups.SummariseAsync(SecurityRollupSubject.AuthProvider, from, before, cancellationToken).ConfigureAwait(false);
        var bySet = await rollups.SummariseAsync(SecurityRollupSubject.RuleSet, from, before, cancellationToken).ConfigureAwait(false);
        Dictionary<AuthProviderId, SecurityPoint> provided = [];
        foreach (var (text, point) in byProvider)
        {
            if (AuthProviderId.TryParse(text, null, out var id))
            {
                provided[id] = point;
            }
        }

        Dictionary<RuleSetId, SecurityPoint> applied = [];
        foreach (var (text, point) in bySet)
        {
            if (RuleSetId.TryParse(text, null, out var id))
            {
                applied[id] = point;
            }
        }

        var read = new SecurityRead(at, totals.GetValueOrDefault(string.Empty) ?? SecurityPoint.Empty(0, 0), provided, applied);
        _cache[range] = new CachedRead(at, recent, read);
        return read;
    }

    private sealed record CachedRead(DateTimeOffset At, DateTimeOffset Recent, SecurityRead Read);

    private sealed record SecurityRead(DateTimeOffset At, SecurityPoint Total, IReadOnlyDictionary<AuthProviderId, SecurityPoint> Providers, IReadOnlyDictionary<RuleSetId, SecurityPoint> Sets)
    {
        /// <summary>What the sampler holds for the last <paramref name="window"/> (at most its hour).</summary>
        public static SecurityRead Of(DateTimeOffset at, MetricSampler sampler, TimeSpan window, DateTimeOffset? notBefore = null) => new(
            at,
            sampler.SummariseSecurity(SecurityRollupSubject.Total, window, notBefore).GetValueOrDefault(Guid.Empty) ?? SecurityPoint.Empty(0, 0),
            sampler.SummariseSecurity(SecurityRollupSubject.AuthProvider, window, notBefore).ToDictionary(static pair => AuthProviderId.FromValue(pair.Key), static pair => pair.Value),
            sampler.SummariseSecurity(SecurityRollupSubject.RuleSet, window, notBefore).ToDictionary(static pair => RuleSetId.FromValue(pair.Key), static pair => pair.Value));

        public SecurityPoint Provider(AuthProviderId id) => Providers.GetValueOrDefault(id) ?? SecurityPoint.Empty(0, 0);

        public SecurityPoint Set(RuleSetId id) => Sets.GetValueOrDefault(id) ?? SecurityPoint.Empty(0, 0);
    }

    // The limit and the deny list are the live config's, and what it counts now is a level; what it refused is over the range.
    private ConnectionCounters ConnectionsOf(RuntimeSnapshot snapshot, SecurityPoint total)
    {
        var policy = snapshot.Connections;
        ConnectionDenyListRef? list = policy.DenyListId is { } id ? new(id, policy.DenyListName ?? string.Empty, policy.Deny.RangeCount) : null;
        return new ConnectionCounters(
            policy.MaxPerIp, total[SecurityCounter.ConnectionsDenied], total[SecurityCounter.ConnectionsOverLimit], total[SecurityCounter.ConnectionsUncounted], connections.Stats.Open, list);
    }

    private static HostProtections HostOf(CompiledHost host, SecurityRead security)
    {
        // Only a proxy host runs the gate on its routes; the other kinds carry the setting inertly.
        var powOn = host.Kind == HostKind.Proxy && host.ProofOfWork.Enabled;
        var powOverrides = host.Kind == HostKind.Proxy ? host.Routes.Count(route => route.ProofOfWork.Enabled != powOn) : 0;

        var hostPolicy = host.RateLimit;
        var overrides = host.Routes
            .Select(route => route.Policies.OfType<RateLimitPolicy>().FirstOrDefault())
            .Where(policy => policy?.PolicyId != hostPolicy?.PolicyId)
            .GroupBy(policy => policy?.PolicyId)
            .Select(group => new RateLimitOverride(group.Count(), group.First() is { } policy ? RefOf(policy) : null))
            .OrderBy(use => use.Policy?.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var hostList = host.AccessList;
        var listOverrides = host.Routes
            .Where(route => route.AccessList?.Id != hostList?.Id)
            .GroupBy(route => route.AccessList?.Id)
            .Select(group => new AccessListOverride(group.Count(), group.First().AccessList is { } list ? ListRefOf(list) : null))
            .OrderBy(use => use.List?.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var hostSet = host.RuleSet;
        var setOverrides = host.Routes
            .Where(route => route.RuleSet?.Id != hostSet?.Id)
            .GroupBy(route => route.RuleSet?.Id)
            .Select(group => new RuleSetOverride(group.Count(), group.First().RuleSet is { } set ? SetRefOf(set, security) : null))
            .OrderBy(use => use.Set?.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var hostProvider = host.AuthProvider;
        var providerOverrides = host.Routes
            .Where(route => route.AuthProvider?.Id != hostProvider?.Id)
            .GroupBy(route => route.AuthProvider?.Id)
            .Select(group => new AuthProviderOverride(group.Count(), group.First().AuthProvider is { } provider ? ProviderRefOf(provider, security) : null))
            .OrderBy(use => use.Provider?.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new HostProtections(
            host.Id,
            host.Names.Count > 0 ? host.Names[0] : string.Empty,
            new ProofOfWorkProtection(powOn, host.ProofOfWork.Difficulty, powOverrides),
            new RateLimitProtection(overrides, hostPolicy is null ? null : RefOf(hostPolicy)),
            host.RobotsMode,
            new AccessListProtection(listOverrides, hostList is null ? null : ListRefOf(hostList)),
            host.OriginLockdown,
            host.AuthenticatedOriginPulls,
            new RuleSetProtection(setOverrides, hostSet is null ? null : SetRefOf(hostSet, security)),
            new AuthProviderProtection(providerOverrides, hostProvider is null ? null : ProviderRefOf(hostProvider, security)));
    }

    private static AuthProviderRef ProviderRefOf(CompiledAuthProvider provider, SecurityRead security)
    {
        var counts = security.Provider(provider.Id);
        return new(
            provider.Id,
            provider.Name,
            provider.Spec.Url,
            counts[SecurityCounter.SignInAllowed],
            counts[SecurityCounter.SignInDenied] + counts[SecurityCounter.SignInRedirected],
            counts[SecurityCounter.SignInErrors] + counts[SecurityCounter.SignInTimeouts] + counts[SecurityCounter.SignInBusy]);
    }

    private static AccessListRef ListRefOf(CompiledAccessList list) =>
        new(list.Id, list.Name, list.UserCount, list.Allow.RangeCount + list.Deny.RangeCount, list.AllowCountries.Count + list.DenyCountries.Count);

    private static RuleSetRef SetRefOf(CompiledRuleSet set, SecurityRead security)
    {
        var counts = security.Set(set.Id);
        return new(
            set.Id, set.Name, set.RuleCount, Contracts.Config.RuleSetPresets.IsPreset(set.Id),
            counts[SecurityCounter.RulesDenied], counts[SecurityCounter.RulesDropped], counts[SecurityCounter.RulesAllowed], counts[SecurityCounter.RulesTimeouts]);
    }

    private static RateLimitPolicyRef RefOf(RateLimitPolicy policy) => new(policy.PolicyId, policy.PolicyName);
}
