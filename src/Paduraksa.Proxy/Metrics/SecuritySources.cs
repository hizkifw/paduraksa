using Paduraksa.Core.Model;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.ForwardAuth;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.RateLimiting;
using Paduraksa.Proxy.RuleSets;

namespace Paduraksa.Proxy.Metrics;

/// <summary>
/// Where the metric sampler reads the gate's security counters each tick (docs/design/observability.md "Security counters"): the
/// process-wide stats and every auth provider's and rule set's counters. All of them only grow for the life of the process, and a
/// provider or set keeps its counters across publishes, so a gain is a plain difference.
/// </summary>
public sealed class SecuritySources(
    ProofOfWorkStats proofOfWork, RateLimitStats rateLimit, AccessListStats accessLists, ConnectionStats? connections = null, ForwardAuthRuntime? forwardAuth = null, RuleSetRuntime? ruleSets = null)
{
    internal ReadOnlySpan<KeyValuePair<AuthProviderId, ForwardAuthCounters>> Providers => forwardAuth is null ? default : forwardAuth.AllCounters;

    internal ReadOnlySpan<KeyValuePair<RuleSetId, RuleSetCounters>> RuleSets => ruleSets is null ? default : ruleSets.All;

    /// <summary>Adds the process-wide counters into <paramref name="into"/> (indexed by <see cref="SecurityCounter"/>).</summary>
    internal void ReadProcess(Span<long> into)
    {
        into[(int)SecurityCounter.ChallengesServed] += proofOfWork.ChallengesServed;
        into[(int)SecurityCounter.Solved] += proofOfWork.Solved;
        into[(int)SecurityCounter.VerifyRejected] += proofOfWork.VerifyRejected;
        into[(int)SecurityCounter.BlockedNonBrowser] += proofOfWork.BlockedNonBrowser;
        into[(int)SecurityCounter.TokenMalformed] += proofOfWork.Malformed;
        into[(int)SecurityCounter.TokenBadTag] += proofOfWork.BadTag;
        into[(int)SecurityCounter.TokenExpired] += proofOfWork.Expired;
        into[(int)SecurityCounter.TokenTooEasy] += proofOfWork.TooEasy;
        into[(int)SecurityCounter.RateLimitAllowed] += rateLimit.Allowed;
        into[(int)SecurityCounter.RateLimited] += rateLimit.Limited;
        into[(int)SecurityCounter.AddressRefused] += accessLists.AddressRefused;
        into[(int)SecurityCounter.Unauthorized] += accessLists.Unauthorized;
        into[(int)SecurityCounter.PasswordCacheHits] += accessLists.CacheHits;
        into[(int)SecurityCounter.Verifications] += accessLists.Verifications;
        into[(int)SecurityCounter.Queued] += accessLists.Queued;
        into[(int)SecurityCounter.QueueFull] += accessLists.QueueFull;
        into[(int)SecurityCounter.QueueTimeouts] += accessLists.QueueTimeouts;
        if (connections is not null)
        {
            into[(int)SecurityCounter.ConnectionsDenied] += connections.Denied;
            into[(int)SecurityCounter.ConnectionsOverLimit] += connections.OverCap;
            into[(int)SecurityCounter.ConnectionsUncounted] += connections.TableFull;
        }
    }

    internal static void Read(ForwardAuthCounters counters, Span<long> into)
    {
        into[(int)SecurityCounter.SignInAllowed] += counters.Allowed;
        into[(int)SecurityCounter.SignInDenied] += counters.Denied;
        into[(int)SecurityCounter.SignInRedirected] += counters.Redirected;
        into[(int)SecurityCounter.SignInCacheHits] += counters.CacheHits;
        into[(int)SecurityCounter.SignInCalls] += counters.Calls;
        into[(int)SecurityCounter.SignInErrors] += counters.Errors;
        into[(int)SecurityCounter.SignInTimeouts] += counters.Timeouts;
        into[(int)SecurityCounter.SignInBusy] += counters.Busy;
    }

    internal static void Read(RuleSetCounters counters, Span<long> into)
    {
        into[(int)SecurityCounter.RulesDenied] += counters.Denied;
        into[(int)SecurityCounter.RulesDropped] += counters.Dropped;
        into[(int)SecurityCounter.RulesAllowed] += counters.Allowed;
        into[(int)SecurityCounter.RulesTimeouts] += counters.Timeouts;
    }
}
