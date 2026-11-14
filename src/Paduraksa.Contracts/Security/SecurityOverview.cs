using Paduraksa.Contracts.Metrics;
using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Security;

/// <summary>
/// What protects the hosts and how much work it has done: the gate's counters over a range, kept in the metrics' rollups so a restart
/// doesn't reset them, the refusals by reason and over time, and each enabled host's protections as the live snapshot compiled them.
/// </summary>
/// <param name="Range">How far back the counters reach: the hour is the sampler's memory (and the hour before a restart), the rest the one-minute rollups.</param>
/// <param name="SampledAt">When it was read.</param>
/// <param name="ProofOfWork">The proof-of-work gate's counters over the range.</param>
/// <param name="RateLimit">The rate limiter's counters over the range.</param>
/// <param name="AccessLists">The access lists' counters over the range.</param>
/// <param name="Hosts">The enabled hosts, by name, with what each refused over the range.</param>
/// <param name="ForwardAuth">Every auth provider's counters over the range, summed, a deleted provider's included.</param>
/// <param name="Connections">The connection stage: its limit and deny list as configured now, what it refused over the range, and what it counts now.</param>
/// <param name="Refusals">What the gate refused over the range, by reason.</param>
/// <param name="Series">The refusals by reason over time, one step per <paramref name="Step"/> (the hour mixes minutes from before a restart with ten-second steps): every step from the first the server observed to the last, a step with <c>coveredSeconds</c> 0 being a time it was not running.</param>
/// <param name="Step">The width of a step of <paramref name="Series"/>.</param>
/// <param name="From">The first moment inside the range the server observed: the counters cover from here to <paramref name="SampledAt"/>, less any time it was down. Absent when it observed none of the range.</param>
public sealed record SecurityOverview(
    MetricsRange Range,
    DateTimeOffset SampledAt,
    ProofOfWorkCounters ProofOfWork,
    RateLimitCounters RateLimit,
    AccessListCounters AccessLists,
    IReadOnlyList<HostProtections> Hosts,
    SignInCounters ForwardAuth,
    ConnectionCounters Connections,
    RefusalReasons Refusals,
    IReadOnlyList<RefusalStep> Series,
    TimeSpan Step,
    DateTimeOffset? From = null);

/// <summary>Requests the gate turned away, by reason. The first nine are the refusals the overview counts as refused; the last four are answers the gate gave without a policy.</summary>
/// <param name="AccessList">An access list refused them by address, country or password.</param>
/// <param name="RuleSet">A rule set blocked them.</param>
/// <param name="RateLimit">A rate limit turned them away.</param>
/// <param name="ProofOfWork">The proof-of-work page, or a refusal of its token.</param>
/// <param name="SignIn">An auth provider refused, redirected or failed.</param>
/// <param name="RequestLimits">Over a request target, header or body limit (413, 414, 431).</param>
/// <param name="Connection">The connection could not serve the host: a 421, the origin lockdown or a missing client certificate.</param>
/// <param name="Other">A policy with no kind of its own.</param>
/// <param name="Unknown">Refusals stored before the reasons were kept: counted, but of an unknown reason.</param>
/// <param name="NoRoute">The host has no route for the path.</param>
/// <param name="Unavailable">The 503 of no live config or no endpoint.</param>
/// <param name="Dropped">The connection was dropped without an answer.</param>
/// <param name="UnknownHost">No host matched the name.</param>
public sealed record RefusalReasons(
    long AccessList,
    long RuleSet,
    long RateLimit,
    long ProofOfWork,
    long SignIn,
    long RequestLimits,
    long Connection,
    long Other,
    long Unknown,
    long NoRoute,
    long Unavailable,
    long Dropped,
    long UnknownHost)
{
    /// <summary>Everything refused: the first nine reasons.</summary>
    public long Refused => AccessList + RuleSet + RateLimit + ProofOfWork + SignIn + RequestLimits + Connection + Other + Unknown;
}

/// <summary>One step of the refusals over time.</summary>
/// <param name="Start">The step's first instant.</param>
/// <param name="WidthSeconds">How long the step is.</param>
/// <param name="CoveredSeconds">How many of its seconds the server observed; 0 is a time it was not running, not a step with no refusals.</param>
/// <param name="Refused">What was refused in it, by reason (counts, not rates).</param>
public sealed record RefusalStep(DateTimeOffset Start, int WidthSeconds, int CoveredSeconds, RefusalReasons Refused);

/// <summary>The connection stage: what runs before the TLS handshake and what it has refused over the range, over TCP and QUIC.</summary>
/// <param name="MaxPerIp">Open connections one client address may hold; 0 means the limit is off. Trusted proxies are exempt.</param>
/// <param name="DenyList">The access list whose deny addresses are refused before TLS; absent when none is named.</param>
/// <param name="DenyListRefused">Connections refused for being in the deny list.</param>
/// <param name="LimitRefused">Connections refused because their address already held the limit.</param>
/// <param name="Uncounted">Connections let through uncounted because the table had no entry for their address; 0 unless a flood from many addresses filled it.</param>
/// <param name="Counted">Connections the limit counts now.</param>
public sealed record ConnectionCounters(int MaxPerIp, long DenyListRefused, long LimitRefused, long Uncounted, long Counted, ConnectionDenyListRef? DenyList = null);

/// <summary>The connection deny list.</summary>
/// <param name="Id">The access list.</param>
/// <param name="Name">Its name.</param>
/// <param name="AddressRanges">The deny address ranges it covers, after merging overlaps.</param>
public sealed record ConnectionDenyListRef(AccessListId Id, string Name, int AddressRanges);

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

/// <summary>The access lists' counters.</summary>
/// <param name="AddressRefused">Requests refused for their address (403, or dropped).</param>
/// <param name="Unauthorized">Requests answered 401: no credentials, or wrong ones.</param>
/// <param name="CacheHits">Passwords accepted from the five-minute memory, without a check.</param>
/// <param name="Verifications">Password checks run.</param>
/// <param name="Queued">Requests that waited for a password check to come free.</param>
/// <param name="QueueFull">Requests refused with 429 at once: the line was full, or their client already had its share of it.</param>
/// <param name="QueueTimeouts">Requests refused with 429 after waiting the maximum.</param>
public sealed record AccessListCounters(long AddressRefused, long Unauthorized, long CacheHits, long Verifications, long Queued, long QueueFull, long QueueTimeouts);

/// <summary>The auth providers' counters, summed over every provider the live config names.</summary>
/// <param name="Allowed">Requests a provider let through, from its answer or from its decision cache.</param>
/// <param name="Denied">Requests a provider refused (a relayed 401, 403 or 407).</param>
/// <param name="Redirected">Requests sent to a sign-in page.</param>
/// <param name="CacheHits">Allows answered from the decision cache, with no call.</param>
/// <param name="Calls">Calls made to the providers' endpoints.</param>
/// <param name="Errors">Calls that failed or got an answer the proxy refuses to act on (502).</param>
/// <param name="Timeouts">Calls that ran out of time (504).</param>
/// <param name="Busy">Requests refused with 503 because too many calls were already running.</param>
public sealed record SignInCounters(long Allowed, long Denied, long Redirected, long CacheHits, long Calls, long Errors, long Timeouts, long Busy);

/// <summary>One enabled host's protections, from the live compiled config.</summary>
/// <param name="Id">The host.</param>
/// <param name="Name">Its first name.</param>
/// <param name="ProofOfWork">Whether visitors must solve a challenge.</param>
/// <param name="RateLimit">The rate-limit policy, if any.</param>
/// <param name="Robots">How the host answers robots.txt.</param>
/// <param name="AccessList">The access list that guards the host, and the routes that differ from it.</param>
/// <param name="OriginLockdown">What the host does with a peer that isn't Cloudflare's; off unless the client-address mode is a Cloudflare one.</param>
/// <param name="AuthenticatedOriginPulls">Whether the host's handshake requires Cloudflare's client certificate (the same condition).</param>
/// <param name="RuleSet">The rule set that applies to the host, and the routes that differ from it; absent when the response comes from an older server.</param>
/// <param name="AuthProvider">The auth provider that asks who a request is, and the routes that differ from it; absent when the response comes from an older server.</param>
/// <param name="Refused">Requests to the host the gate refused over the range (any reason <see cref="RefusalReasons.Refused"/> counts).</param>
public sealed record HostProtections(
    HostId Id,
    string Name,
    ProofOfWorkProtection ProofOfWork,
    RateLimitProtection RateLimit,
    RobotsMode Robots,
    AccessListProtection AccessList,
    OriginLockdownMode OriginLockdown = OriginLockdownMode.Off,
    bool AuthenticatedOriginPulls = false,
    RuleSetProtection? RuleSet = null,
    AuthProviderProtection? AuthProvider = null,
    long Refused = 0);

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

/// <summary>A host's access-list setting.</summary>
/// <param name="Overrides">The routes that differ from the host's list, grouped by what guards them instead.</param>
/// <param name="List">The list the host names; absent when it names none.</param>
public sealed record AccessListProtection(IReadOnlyList<AccessListOverride> Overrides, AccessListRef? List = null);

/// <summary>Routes guarded by another access list, or none.</summary>
/// <param name="Routes">How many routes.</param>
/// <param name="List">The list those routes apply; absent when they are not guarded.</param>
public sealed record AccessListOverride(int Routes, AccessListRef? List = null);

/// <summary>A named access list, with what it asks of a visitor.</summary>
/// <param name="Id">The list.</param>
/// <param name="Name">Its name.</param>
/// <param name="Users">How many users it asks a password of; 0 means addresses only.</param>
/// <param name="AddressRanges">The address ranges its allow and deny entries cover, after merging overlaps.</param>
/// <param name="Countries">The countries (and Tor or unknown) its allow and deny lists name.</param>
public sealed record AccessListRef(AccessListId Id, string Name, int Users, int AddressRanges, int Countries = 0);

/// <summary>A host's rule-set setting.</summary>
/// <param name="Overrides">The routes that differ from the host's set, grouped by what applies to them instead.</param>
/// <param name="Set">The set the host names; absent when it names none.</param>
public sealed record RuleSetProtection(IReadOnlyList<RuleSetOverride> Overrides, RuleSetRef? Set = null);

/// <summary>Routes that apply another rule set, or none.</summary>
/// <param name="Routes">How many routes.</param>
/// <param name="Set">The set those routes apply; absent when they apply none.</param>
public sealed record RuleSetOverride(int Routes, RuleSetRef? Set = null);

/// <summary>A named rule set with what it has done since the process started, across every host and route that applies it.</summary>
/// <param name="Id">The set.</param>
/// <param name="Name">Its name.</param>
/// <param name="Rules">How many rules it has.</param>
/// <param name="BuiltIn">It is one of the built-in presets, which have no page of their own to open.</param>
/// <param name="Denied">Requests a rule refused with 403 over the range, across every host that applies the set.</param>
/// <param name="Dropped">Requests a rule dropped without an answer.</param>
/// <param name="Allowed">Requests an allow rule took out of the set's later rules.</param>
/// <param name="Timeouts">Regex matches that ran past their timeout and were decided by the rule's action (a deny or drop counts as a match, an allow as none); 0 in a healthy proxy.</param>
public sealed record RuleSetRef(RuleSetId Id, string Name, int Rules, bool BuiltIn, long Denied, long Dropped, long Allowed, long Timeouts = 0);

/// <summary>A host's auth-provider setting.</summary>
/// <param name="Overrides">The routes that differ from the host's provider, grouped by what asks them instead.</param>
/// <param name="Provider">The provider the host names; absent when it names none.</param>
public sealed record AuthProviderProtection(IReadOnlyList<AuthProviderOverride> Overrides, AuthProviderRef? Provider = null);

/// <summary>Routes that ask another auth provider, or none.</summary>
/// <param name="Routes">How many routes.</param>
/// <param name="Provider">The provider those routes ask; absent when they ask none.</param>
public sealed record AuthProviderOverride(int Routes, AuthProviderRef? Provider = null);

/// <summary>A named auth provider with what it has decided since the process started, across every host and route that asks it.</summary>
/// <param name="Id">The provider.</param>
/// <param name="Name">Its name.</param>
/// <param name="Endpoint">Its endpoint.</param>
/// <param name="Allowed">Requests it let through over the range, from its answer or its decision cache, across every host that asks it.</param>
/// <param name="Refused">Requests it refused or sent to a sign-in page.</param>
/// <param name="Failures">Calls that failed, timed out or were refused for being too many.</param>
public sealed record AuthProviderRef(AuthProviderId Id, string Name, string Endpoint, long Allowed, long Refused, long Failures);
