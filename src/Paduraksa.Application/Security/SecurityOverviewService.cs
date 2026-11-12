using Paduraksa.Contracts.Security;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.ForwardAuth;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.RateLimiting;
using Paduraksa.Proxy.RuleSets;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Application.Security;

/// <summary>
/// The security overview: the gate's process counters and the protections the live snapshot applies per host. Reading
/// takes no lock and touches no store, so a page can sample it every couple of seconds; nothing is kept between reads.
/// </summary>
/// <remarks>
/// Protections come from the compiled snapshot rather than the stored document, so they are what the gate is doing:
/// a revision the compiler rejected never shows. The counters start at zero with the process, which is why
/// <see cref="SecurityOverview.Since"/> is the time this service was built at startup.
/// </remarks>
public sealed class SecurityOverviewService
{
    private readonly SnapshotStore _snapshots;
    private readonly ProofOfWorkStats _proofOfWork;
    private readonly RateLimitStats _rateLimit;
    private readonly AccessListStats _accessLists;
    private readonly ConnectionStats _connections;
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _since;

    public SecurityOverviewService(SnapshotStore snapshots, ProofOfWorkRuntime proofOfWork, RateLimitRuntime rateLimits, TimeProvider time, AccessListRuntime accessLists, ConnectionRuntime? connections = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(proofOfWork);
        ArgumentNullException.ThrowIfNull(rateLimits);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(accessLists);
        _snapshots = snapshots;
        _proofOfWork = proofOfWork.Stats;
        _rateLimit = rateLimits.Stats;
        _accessLists = accessLists.Stats;
        _connections = (connections ?? ConnectionRuntime.Default).Stats;
        _time = time;
        _since = time.GetUtcNow();
    }

    public SecurityOverview Read()
    {
        var sampledAt = _time.GetUtcNow();
        var pow = _proofOfWork;
        ProofOfWorkCounters proofOfWork = new(
            pow.ChallengesServed, pow.Solved, pow.VerifyRejected, pow.BlockedNonBrowser,
            new TokenRejections(pow.Malformed, pow.BadTag, pow.Expired, pow.TooEasy));
        RateLimitCounters rateLimit = new(_rateLimit.Allowed, _rateLimit.Limited);
        var lists = _accessLists;
        AccessListCounters accessLists = new(
            lists.AddressRefused, lists.Unauthorized, lists.CacheHits, lists.Verifications, lists.Queued, lists.QueueFull, lists.QueueTimeouts);
        var current = _snapshots.Current;
        var hosts = current.HostsByIndex
            .Select(HostOf)
            .OrderBy(host => host.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(host => host.Id.ToString(), StringComparer.Ordinal)
            .ToArray();
        return new SecurityOverview(_since, sampledAt, proofOfWork, rateLimit, accessLists, hosts, ForwardAuthOf(current), ConnectionsOf(current));
    }

    private ConnectionCounters ConnectionsOf(RuntimeSnapshot snapshot)
    {
        var policy = snapshot.Connections;
        var stats = _connections;
        ConnectionDenyListRef? list = policy.DenyListId is { } id ? new(id, policy.DenyListName ?? string.Empty, policy.Deny.RangeCount) : null;
        return new ConnectionCounters(policy.MaxPerIp, list, stats.Denied, stats.OverCap, stats.TableFull, stats.Open);
    }

    // Each provider the live config applies counts once, however many hosts and routes ask it.
    private static SignInCounters ForwardAuthOf(RuntimeSnapshot snapshot)
    {
        Dictionary<AuthProviderId, CompiledAuthProvider> providers = [];
        foreach (var host in snapshot.HostsByIndex)
        {
            if (host.AuthProvider is { } hostProvider)
            {
                providers.TryAdd(hostProvider.Id, hostProvider);
            }

            foreach (var route in host.Routes)
            {
                if (route.AuthProvider is { } routeProvider)
                {
                    providers.TryAdd(routeProvider.Id, routeProvider);
                }
            }
        }

        long allowed = 0, denied = 0, redirected = 0, cacheHits = 0, calls = 0, errors = 0, timeouts = 0, busy = 0;
        foreach (var provider in providers.Values)
        {
            var counters = provider.Counters;
            allowed += counters.Allowed;
            denied += counters.Denied;
            redirected += counters.Redirected;
            cacheHits += counters.CacheHits;
            calls += counters.Calls;
            errors += counters.Errors;
            timeouts += counters.Timeouts;
            busy += counters.Busy;
        }

        return new SignInCounters(allowed, denied, redirected, cacheHits, calls, errors, timeouts, busy);
    }

    private static HostProtections HostOf(CompiledHost host)
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
            .Select(group => new RuleSetOverride(group.Count(), group.First().RuleSet is { } set ? SetRefOf(set) : null))
            .OrderBy(use => use.Set?.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var hostProvider = host.AuthProvider;
        var providerOverrides = host.Routes
            .Where(route => route.AuthProvider?.Id != hostProvider?.Id)
            .GroupBy(route => route.AuthProvider?.Id)
            .Select(group => new AuthProviderOverride(group.Count(), group.First().AuthProvider is { } provider ? ProviderRefOf(provider) : null))
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
            new RuleSetProtection(setOverrides, hostSet is null ? null : SetRefOf(hostSet)),
            new AuthProviderProtection(providerOverrides, hostProvider is null ? null : ProviderRefOf(hostProvider)));
    }

    private static AuthProviderRef ProviderRefOf(CompiledAuthProvider provider)
    {
        var counters = provider.Counters;
        return new(provider.Id, provider.Name, provider.Spec.Url, counters.Allowed, counters.Denied + counters.Redirected, counters.Errors + counters.Timeouts + counters.Busy);
    }

    private static AccessListRef ListRefOf(CompiledAccessList list) =>
        new(list.Id, list.Name, list.UserCount, list.Allow.RangeCount + list.Deny.RangeCount, list.AllowCountries.Count + list.DenyCountries.Count);

    private static RuleSetRef SetRefOf(CompiledRuleSet set) =>
        new(set.Id, set.Name, set.RuleCount, Contracts.Config.RuleSetPresets.IsPreset(set.Id), set.Counters.Denied, set.Counters.Dropped, set.Counters.Allowed, set.Counters.Timeouts);

    private static RateLimitPolicyRef RefOf(RateLimitPolicy policy) => new(policy.PolicyId, policy.PolicyName);
}
