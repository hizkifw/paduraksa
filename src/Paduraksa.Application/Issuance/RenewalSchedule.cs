using Paduraksa.Acme;

namespace Paduraksa.Application.Issuance;

/// <summary>
/// The scheduler's arithmetic (PLAN §9.3), free of clocks and randomness so it can be tested exactly: every random share
/// is passed in as a number in [0, 1).
/// </summary>
internal static class RenewalSchedule
{
    /// <summary>
    /// When to start renewing a certificate. With an ARI window (RFC 9773 §4.2): a uniformly random time in it
    /// (<paramref name="random"/> picks the point), but never later than the lifetime rule below would renew (a CA's window must not
    /// push a renewal to the moment of expiry). Without one: when
    /// <see cref="AcmeIssuanceOptions.RenewalRemainingFraction"/> of its lifetime is left, moved earlier by up to
    /// <see cref="AcmeIssuanceOptions.RenewalJitterFraction"/> of that period. The result may be in the past (renew now).
    /// </summary>
    public static DateTimeOffset NextRenewalAt(
        DateTimeOffset notBefore, DateTimeOffset notAfter, AcmeRenewalInfo? renewalInfo, double random, AcmeIssuanceOptions options)
    {
        if (renewalInfo is not null)
        {
            DateTimeOffset pick = renewalInfo.PickTime(Math.Clamp(random, 0d, 1d));
            DateTimeOffset latest = notAfter - ((notAfter - notBefore) * options.RenewalRemainingFraction);
            return pick < latest ? pick : latest;
        }

        TimeSpan lifetime = notAfter - notBefore;
        TimeSpan remaining = lifetime * options.RenewalRemainingFraction;
        TimeSpan jitter = remaining * (options.RenewalJitterFraction * Math.Clamp(random, 0d, 1d));
        return notAfter - remaining - jitter;
    }

    /// <summary>
    /// Whether an earlier pick is still good after a fresh ARI answer: a pick inside the new window stays (so a routine
    /// recheck does not re-roll the dice and drift later), anything else is picked again.
    /// </summary>
    public static bool KeepsPick(DateTimeOffset? current, AcmeRenewalInfo renewalInfo) =>
        current is { } pick && pick >= renewalInfo.WindowStart && pick <= renewalInfo.WindowEnd;

    /// <summary>When ARI is asked again: the CA's <c>Retry-After</c> if it sent one, bounded by the options.</summary>
    public static DateTimeOffset NextAriCheck(DateTimeOffset now, TimeSpan? retryAfter, AcmeIssuanceOptions options)
    {
        TimeSpan wait = retryAfter ?? options.AriDefaultRecheck;
        if (wait < options.AriMinRecheck)
        {
            wait = options.AriMinRecheck;
        }
        else if (wait > options.AriMaxRecheck)
        {
            wait = options.AriMaxRecheck;
        }

        return now + wait;
    }

    /// <summary>
    /// How long to wait after the <paramref name="attempts"/>th failure in a row: exponential from the base of the
    /// failure's kind up to its cap, jittered; for <see cref="AcmeFailureKind.RateLimited"/> never less than the CA's
    /// <paramref name="retryAfter"/> (capped by <see cref="AcmeIssuanceOptions.RateLimitBackoffCap"/>).
    /// </summary>
    public static TimeSpan Backoff(AcmeFailureKind kind, int attempts, TimeSpan? retryAfter, double random, AcmeIssuanceOptions options)
    {
        (TimeSpan first, TimeSpan cap) = kind switch
        {
            AcmeFailureKind.Transient => (options.TransientBackoff, options.TransientBackoffCap),
            AcmeFailureKind.RateLimited => (options.RateLimitBackoff, options.RateLimitBackoffCap),
            AcmeFailureKind.ProtocolViolation => (options.ProtocolBackoff, options.ProtocolBackoffCap),
            _ => (options.PermanentBackoff, options.PermanentBackoffCap),
        };

        // The exponential part stops where jitter can still reach the cap but not pass it, so the jitter stays symmetric at the cap.
        double spread = options.BackoffJitterFraction;
        double ceiling = cap.Ticks / (1d + spread);
        int doublings = Math.Clamp(attempts - 1, 0, 20);
        double jitter = 1d + (spread * ((2d * Math.Clamp(random, 0d, 1d)) - 1d));
        TimeSpan wait = TimeSpan.FromTicks((long)(Math.Min(first.Ticks * Math.Pow(2, doublings), ceiling) * jitter));
        if (kind == AcmeFailureKind.RateLimited && retryAfter is { } asked && asked > wait)
        {
            wait = asked;
        }

        return wait > cap ? cap : wait;
    }
}
