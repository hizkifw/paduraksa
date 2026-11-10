using System.Buffers;
using System.Globalization;

using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Pages;
using Paduraksa.Proxy.RateLimiting;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Proxy.Gate;

/// <summary>
/// One (policy, host) rate limit in a route's chain: takes a token from the bucket the request's partition key selects,
/// or answers 429 with the wait in <c>Retry-After</c>. It runs after the route's access policies and before the
/// proof-of-work gate, so a client that is over its budget gets the 429 rather than a challenge page (PLAN §7.2 D).
/// </summary>
/// <remarks>
/// <para><b>Bucket scope.</b> The slot is the (policy ID, host ID) pair's, fixed at compile time, so two hosts that
/// name one policy don't share a client's budget, while every route of one host that resolves to the same policy does
/// share it. The policy's rate and burst are precomputed too, so an edit applies on the next touch without moving a
/// bucket (PLAN §6.3). A pair with slot 0 exists only in a candidate that is never published (a draft, a dry run or a
/// simulation): there is no bucket to touch, and the simulator reports the fresh one it would get.</para>
/// <para><b>Partition.</b> <see cref="RateLimitKey.ClientIp"/> keys the resolved client address (IPv6 by /64);
/// <see cref="RateLimitKey.ClientIpAndPath"/> adds the canonical matching path, the same view the route matched on;
/// <see cref="RateLimitKey.Header"/> keys the header's value. A header that is absent or empty falls back to the client
/// IP, and the partitioner domain-separates the kinds, so a header value can't collide with an address.</para>
/// <para><b>Allocation.</b> The allowed and refused paths allocate nothing: the parameters and the <c>Retry-After</c>
/// values are precomputed, the partition hash runs over spans, and a non-canonical path is rewritten on the stack or in a
/// pooled buffer. A simulation peeks instead of taking a token, so it changes nothing.</para>
/// </remarks>
public sealed class RateLimitPolicy : IRequestPolicy
{
    private static readonly PrecomputedResponse TooManyRequests =
        PrecomputedResponse.Text(StatusCodes.Status429TooManyRequests, "Too many requests. Please slow down.\n", htmlPage: ErrorPages.TooManyRequests);

    private readonly RateLimitTable _table;
    private readonly RateLimitPartition _partition;
    private readonly RateLimitStats _stats;
    private readonly RateLimitParameters _parameters;
    private readonly RateLimitKey _key;
    private readonly string? _header;
    private readonly string _policyName;
    private readonly bool _routeOverride;
    private readonly ushort _slot;
    private readonly string[] _retryAfter;

    internal RateLimitPolicy(
        RateLimitPolicySpec policy,
        ushort slot,
        string? header,
        bool routeOverride,
        RateLimitTable table,
        RateLimitPartition partition,
        RateLimitStats stats)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(stats);
        _policyName = policy.Name;
        _parameters = policy.Parameters;
        _key = policy.Key;
        _header = header;
        _routeOverride = routeOverride;
        _slot = slot;
        _table = table;
        _partition = partition;
        _stats = stats;

        // Every value TryAcquire can refuse with, so the refusal path never formats a string.
        var max = _parameters.MaxRetryAfterSeconds;
        _retryAfter = new string[max + 1];
        for (var seconds = 1; seconds <= max; seconds++)
        {
            _retryAfter[seconds] = seconds.ToString(CultureInfo.InvariantCulture);
        }
    }

    public PolicyDecision Evaluate(in RequestView request)
    {
        if (_slot == 0)
        {
            // No slot: the pair exists only in a candidate that was never published (a draft, a dry run or a simulation),
            // so there is no bucket to spend. Let it through rather than throw; the gate never runs such a snapshot.
            return PolicyDecision.Continue;
        }

        var now = RateLimitClock.NowMs;
        var partition = Partition(in request);
        if (request.IsSimulation)
        {
            // Peek: a simulation must leave the bucket as it found it.
            return _table.TryPeek(_slot, partition, in _parameters, now, out _, out var wait)
                ? PolicyDecision.Continue
                : PolicyDecision.Respond(TooManyRequests, RetryAfterText(wait));
        }

        if (_table.TryAcquire(_slot, partition, in _parameters, now, out var retryAfter))
        {
            _stats.CountAllowed();
            return PolicyDecision.Continue;
        }

        _stats.CountLimited();
        return PolicyDecision.Respond(TooManyRequests, RetryAfterText(retryAfter));
    }

    string IRequestPolicy.Name => "rateLimit";

    string IRequestPolicy.Explain(in RequestView request, PolicyDecision verdict)
    {
        var keyed = _key switch
        {
            RateLimitKey.ClientIpAndPath => "client IP and path",
            RateLimitKey.Header => _header is { } header ? $"header '{header}' (client IP when it is absent)" : "client IP",
            _ => "client IP",
        };
        var source = _routeOverride ? "the route's own override; " : string.Empty;
        if (_slot == 0)
        {
            return string.Concat(
                source,
                "would allow, full burst (new policy): policy '",
                _policyName,
                "', keyed by ",
                keyed);
        }

        var partition = Partition(in request);
        var now = RateLimitClock.NowMs;
        if (_table.TryPeek(_slot, partition, in _parameters, now, out var tokensLeft, out var retryAfter))
        {
            return string.Concat(
                source,
                "would allow, ",
                tokensLeft.ToString(CultureInfo.InvariantCulture),
                tokensLeft == 1 ? " token left" : " tokens left",
                " in policy '",
                _policyName,
                "', keyed by ",
                keyed);
        }

        return string.Concat(
            source,
            "would refuse, retry after ",
            retryAfter.ToString(CultureInfo.InvariantCulture),
            " s: policy '",
            _policyName,
            "', keyed by ",
            keyed);
    }

    /// <summary>The request's 48-bit partition hash; no allocation (a path rewrite is on the stack or pooled).</summary>
    private ulong Partition(in RequestView request)
    {
        var client = request.State.ClientAddress;
        switch (_key)
        {
            case RateLimitKey.ClientIpAndPath:
                return PathPartition(client, request.Http.Request.Path.Value.AsSpan());
            case RateLimitKey.Header:
                var values = request.Http.Request.Headers[_header!];
                if (values.Count == 0 || string.IsNullOrEmpty(values[0]))
                {
                    // No header value is no key of its own: fall back to the address, in another hash domain.
                    return _partition.ForIp(client);
                }

                return _partition.ForHeader(values[0].AsSpan());
            default:
                return _partition.ForIp(client);
        }
    }

    private ulong PathPartition(IpKey client, ReadOnlySpan<char> path)
    {
        if (RoutePath.IsCanonical(path))
        {
            return _partition.ForIpAndPath(client, path);
        }

        if (path.Length < RoutePath.StackLimit)
        {
            Span<char> buffer = stackalloc char[RoutePath.StackLimit];
            var length = RoutePath.Canonicalize(path, buffer);
            return _partition.ForIpAndPath(client, buffer[..length]);
        }

        var rented = ArrayPool<char>.Shared.Rent(path.Length + 1);
        try
        {
            var length = RoutePath.Canonicalize(path, rented);
            return _partition.ForIpAndPath(client, rented.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>The precomputed <c>Retry-After</c> value; a value past the table can't occur but is clamped rather than
    /// throwing.</summary>
    private string RetryAfterText(int retryAfterSeconds) =>
        _retryAfter[Math.Clamp(retryAfterSeconds, 1, _retryAfter.Length - 1)];
}
