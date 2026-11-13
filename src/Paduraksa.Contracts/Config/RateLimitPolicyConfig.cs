using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Config;

/// <summary>
/// A named token bucket that hosts and routes apply: <see cref="Requests"/> per <see cref="Per"/> seconds, with a
/// bucket of <see cref="Burst"/> tokens, keyed the way <see cref="Key"/> says. It is a top-level object, so two hosts
/// can name one policy and still keep their own buckets (docs/design/config.md "State that survives a publish").
/// </summary>
/// <param name="Id">Identity; the first half of a bucket's stable slot identity.</param>
/// <param name="Name">Human label, unique ignoring case.</param>
/// <param name="Version">Concurrency version.</param>
/// <param name="Requests">Whole requests per <paramref name="Per"/>, at least 1. The token rate is this over the
/// period's seconds.</param>
/// <param name="Per">The period the rate is measured over.</param>
/// <param name="Burst">The bucket's size in tokens, at least 1: how many requests may arrive at once before the rate
/// has to earn more.</param>
/// <param name="Key">What one bucket belongs to: a client IP, an IP and path, or a header value.</param>
/// <param name="Header">With <see cref="RateLimitKey.Header"/>: the request header whose value is the partition key.
/// Required exactly then, and a valid header name (a token).</param>
/// <param name="Notes">Free text.</param>
public sealed record RateLimitPolicyConfig(
    RateLimitPolicyId Id,
    string Name,
    long Version = 0,
    int Requests = 60,
    RateLimitPeriod Per = RateLimitPeriod.Minute,
    int Burst = 60,
    RateLimitKey Key = RateLimitKey.ClientIp,
    string? Header = null,
    string? Notes = null);
