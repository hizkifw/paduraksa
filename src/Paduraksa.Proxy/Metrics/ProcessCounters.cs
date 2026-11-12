using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.RateLimiting;

namespace Paduraksa.Proxy.Metrics;

/// <summary>One process-lifetime counter (or, with <paramref name="Gauge"/>, a current level) that already exists, read where it lives and exposed as it is.</summary>
/// <param name="Name">The instrument name, dotted (<c>paduraksa.rate_limit.decisions</c>); the Prometheus name is the same with underscores and <c>_total</c>.</param>
/// <param name="Help">One line for Prometheus <c>HELP</c> and the instrument description.</param>
/// <param name="LabelName">The one label that tells the counters of a family apart, or null.</param>
/// <param name="LabelValue">Its value for this counter.</param>
/// <param name="Read">The current total (or level).</param>
/// <param name="Gauge">A level that goes up and down (a queue's depth), exposed as a gauge without the counter's <c>_total</c>.</param>
public sealed record ProcessCounter(string Name, string Help, string? LabelName, string? LabelValue, Func<long> Read, bool Gauge = false);

/// <summary>
/// The counters the security page already reads (the proof-of-work gate, the rate limiter, the access lists), offered to the exporters so nothing is counted twice: these are the process's totals, while the
/// registry's per-host outcomes say where they happened.
/// </summary>
public sealed class ProcessCounters
{
    /// <summary>No process counters: a test, or a process without those runtimes.</summary>
    public static ProcessCounters None { get; } = new([]);

    public ProcessCounters(IReadOnlyList<ProcessCounter> counters)
    {
        ArgumentNullException.ThrowIfNull(counters);
        All = counters;
    }

    public IReadOnlyList<ProcessCounter> All { get; }

    public static ProcessCounters Create(RateLimitStats rateLimit, ProofOfWorkStats proofOfWork, AccessListStats accessLists)
    {
        ArgumentNullException.ThrowIfNull(rateLimit);
        ArgumentNullException.ThrowIfNull(proofOfWork);
        ArgumentNullException.ThrowIfNull(accessLists);
        const string RateLimitName = "paduraksa.rate_limit.decisions";
        const string ProofOfWorkName = "paduraksa.proof_of_work.events";
        const string AccessListName = "paduraksa.access_list.events";
        const string RateLimitHelp = "Rate-limit decisions by result.";
        const string ProofOfWorkHelp = "Proof-of-work gate events: challenges served, solutions accepted and refusals.";
        const string AccessListHelp = "Access-list events: refusals by address or password, password checks and what waited for one.";
        return new(
        [
            new(RateLimitName, RateLimitHelp, "result", "allowed", () => rateLimit.Allowed),
            new(RateLimitName, RateLimitHelp, "result", "limited", () => rateLimit.Limited),
            new(ProofOfWorkName, ProofOfWorkHelp, "event", "challenge_served", () => proofOfWork.ChallengesServed),
            new(ProofOfWorkName, ProofOfWorkHelp, "event", "solved", () => proofOfWork.Solved),
            new(ProofOfWorkName, ProofOfWorkHelp, "event", "verify_rejected", () => proofOfWork.VerifyRejected),
            new(ProofOfWorkName, ProofOfWorkHelp, "event", "blocked_non_browser", () => proofOfWork.BlockedNonBrowser),
            new(AccessListName, AccessListHelp, "event", "allowed", () => accessLists.Allowed),
            new(AccessListName, AccessListHelp, "event", "address_refused", () => accessLists.AddressRefused),
            new(AccessListName, AccessListHelp, "event", "unauthorized", () => accessLists.Unauthorized),
            new(AccessListName, AccessListHelp, "event", "cache_hit", () => accessLists.CacheHits),
            new(AccessListName, AccessListHelp, "event", "verification", () => accessLists.Verifications),
            new(AccessListName, AccessListHelp, "event", "queued", () => accessLists.Queued),
            new(AccessListName, AccessListHelp, "event", "queue_full", () => accessLists.QueueFull),
        ]);
    }
}
