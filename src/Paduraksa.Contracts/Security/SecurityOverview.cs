using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Security;

/// <summary>
/// What protects the hosts and how much work it has done: the gate's counters since the process started, and each
/// enabled host's protections as the live snapshot compiled them. Counters only grow, so two samples give a rate.
/// </summary>
/// <param name="Since">When the counters started: the process start.</param>
/// <param name="SampledAt">When the counters were read.</param>
/// <param name="ProofOfWork">The proof-of-work gate's counters.</param>
/// <param name="RateLimit">The rate limiter's counters.</param>
/// <param name="Hosts">The enabled hosts, by name.</param>
public sealed record SecurityOverview(
    DateTimeOffset Since,
    DateTimeOffset SampledAt,
    ProofOfWorkCounters ProofOfWork,
    RateLimitCounters RateLimit,
    IReadOnlyList<HostProtections> Hosts);

/// <summary>The proof-of-work gate's counters.</summary>
/// <param name="ChallengesServed">Challenge pages sent to browsers without a valid token.</param>
/// <param name="Solved">Challenges answered with a cookie.</param>
/// <param name="VerifyRejected">Answers to the verify endpoint that were refused: a bad challenge or solution.</param>
/// <param name="BlockedNonBrowser">Requests refused with 403 for having no token and not coming from a browser.</param>
/// <param name="TokensRejected">Cookies that were not accepted, by reason.</param>
public sealed record ProofOfWorkCounters(long ChallengesServed, long Solved, long VerifyRejected, long BlockedNonBrowser, TokenRejections TokensRejected);

/// <summary>Why proof-of-work cookies were not accepted.</summary>
/// <param name="Malformed">Not a token this build issues.</param>
/// <param name="BadTag">Altered, or issued for another host or client address.</param>
/// <param name="Expired">Valid, but past its lifetime.</param>
/// <param name="TooEasy">Valid, but easier than the host's current difficulty.</param>
public sealed record TokenRejections(long Malformed, long BadTag, long Expired, long TooEasy);

/// <summary>The rate limiter's counters.</summary>
/// <param name="Allowed">Requests a policy let through.</param>
/// <param name="Limited">Requests a policy refused with 429.</param>
public sealed record RateLimitCounters(long Allowed, long Limited);

/// <summary>One enabled host's protections, from the live compiled config.</summary>
/// <param name="Id">The host.</param>
/// <param name="Name">Its first name.</param>
/// <param name="ProofOfWork">Whether visitors must solve a challenge.</param>
/// <param name="RateLimit">The rate-limit policy, if any.</param>
/// <param name="Robots">How the host answers robots.txt.</param>
/// <param name="AllowListRanges">The address ranges the IP allow list admits, after merging overlaps; 0 when the host has no list.</param>
/// <param name="OriginLockdown">What the host does with a peer that isn't Cloudflare's; off unless the client-address mode is a Cloudflare one.</param>
/// <param name="AuthenticatedOriginPulls">Whether the host's handshake requires Cloudflare's client certificate (the same condition).</param>
public sealed record HostProtections(
    HostId Id,
    string Name,
    ProofOfWorkProtection ProofOfWork,
    RateLimitProtection RateLimit,
    RobotsMode Robots,
    int AllowListRanges,
    OriginLockdownMode OriginLockdown = OriginLockdownMode.Off,
    bool AuthenticatedOriginPulls = false);

/// <summary>A host's proof-of-work setting.</summary>
/// <param name="Enabled">The host's own switch.</param>
/// <param name="Difficulty">Leading zero bits a solution needs (a route can't change it).</param>
/// <param name="RouteOverrides">Routes whose switch differs from the host's: exempt when the host is on, protected when it is off.</param>
public sealed record ProofOfWorkProtection(bool Enabled, int Difficulty, int RouteOverrides);

/// <summary>A host's rate-limit setting.</summary>
/// <param name="Overrides">The routes that differ from the host's policy, grouped by what they apply instead.</param>
/// <param name="Policy">The policy the host applies; absent when it names none.</param>
public sealed record RateLimitProtection(IReadOnlyList<RateLimitOverride> Overrides, RateLimitPolicyRef? Policy = null);

/// <summary>Routes that apply another rate-limit policy, or none.</summary>
/// <param name="Routes">How many routes.</param>
/// <param name="Policy">The policy those routes apply; absent when they are not limited.</param>
public sealed record RateLimitOverride(int Routes, RateLimitPolicyRef? Policy = null);

/// <summary>A named rate-limit policy.</summary>
/// <param name="Id">The policy.</param>
/// <param name="Name">Its name.</param>
public sealed record RateLimitPolicyRef(RateLimitPolicyId Id, string Name);
