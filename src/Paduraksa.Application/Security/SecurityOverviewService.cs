using Paduraksa.Contracts.Security;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.RateLimiting;
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
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _since;

    public SecurityOverviewService(SnapshotStore snapshots, ProofOfWorkRuntime proofOfWork, RateLimitRuntime rateLimits, TimeProvider time, AccessListRuntime accessLists)
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
        var hosts = _snapshots.Current.HostsByIndex
            .Select(HostOf)
            .OrderBy(host => host.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(host => host.Id.ToString(), StringComparer.Ordinal)
            .ToArray();
        return new SecurityOverview(_since, sampledAt, proofOfWork, rateLimit, accessLists, hosts);
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
        return new HostProtections(
            host.Id,
            host.Names.Count > 0 ? host.Names[0] : string.Empty,
            new ProofOfWorkProtection(powOn, host.ProofOfWork.Difficulty, powOverrides),
            new RateLimitProtection(overrides, hostPolicy is null ? null : RefOf(hostPolicy)),
            host.RobotsMode,
            new AccessListProtection(listOverrides, hostList is null ? null : ListRefOf(hostList)),
            host.OriginLockdown,
            host.AuthenticatedOriginPulls);
    }

    private static AccessListRef ListRefOf(CompiledAccessList list) =>
        new(list.Id, list.Name, list.UserCount, list.Allow.RangeCount + list.Deny.RangeCount);

    private static RateLimitPolicyRef RefOf(RateLimitPolicy policy) => new(policy.PolicyId, policy.PolicyName);
}
