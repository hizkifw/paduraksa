namespace Paduraksa.Proxy.Metrics;

/// <summary>
/// The gate's security counters a <see cref="SecurityPoint"/> holds, by index. The process counters (proof of work, rate limits, access
/// lists, the connection stage) only exist in the totals; the sign-in and rule counters are the sum over every provider and set in the
/// totals, and one provider's or set's own in theirs. Appending is safe; the rollup table has a column per name.
/// </summary>
public enum SecurityCounter
{
    /// <summary>Proof-of-work challenge pages sent.</summary>
    ChallengesServed,

    /// <summary>Challenges answered with a cookie.</summary>
    Solved,

    /// <summary>Answers to the verify endpoint that were refused.</summary>
    VerifyRejected,

    /// <summary>Requests refused for having no token and not coming from a browser.</summary>
    BlockedNonBrowser,

    /// <summary>Cookies that are not a token this build issues.</summary>
    TokenMalformed,

    /// <summary>Cookies altered, or issued for another host or client address.</summary>
    TokenBadTag,

    /// <summary>Cookies past their lifetime.</summary>
    TokenExpired,

    /// <summary>Cookies easier than the host's current difficulty.</summary>
    TokenTooEasy,

    /// <summary>Requests a rate-limit policy let through.</summary>
    RateLimitAllowed,

    /// <summary>Requests a rate-limit policy refused with 429.</summary>
    RateLimited,

    /// <summary>Requests an access list refused for their address.</summary>
    AddressRefused,

    /// <summary>Requests an access list answered 401.</summary>
    Unauthorized,

    /// <summary>Passwords accepted from the five-minute memory.</summary>
    PasswordCacheHits,

    /// <summary>Password checks run.</summary>
    Verifications,

    /// <summary>Requests that waited for a password check.</summary>
    Queued,

    /// <summary>Requests refused with 429 at once: the line for a check was full.</summary>
    QueueFull,

    /// <summary>Requests refused with 429 after waiting the maximum.</summary>
    QueueTimeouts,

    /// <summary>Connections refused before TLS for being in the deny list.</summary>
    ConnectionsDenied,

    /// <summary>Connections refused because their address already held the limit.</summary>
    ConnectionsOverLimit,

    /// <summary>Connections let through uncounted because the table was full.</summary>
    ConnectionsUncounted,

    /// <summary>Requests an auth provider let through.</summary>
    SignInAllowed,

    /// <summary>Requests an auth provider refused.</summary>
    SignInDenied,

    /// <summary>Requests sent to a sign-in page.</summary>
    SignInRedirected,

    /// <summary>Allows answered from the decision cache.</summary>
    SignInCacheHits,

    /// <summary>Calls made to the providers' endpoints.</summary>
    SignInCalls,

    /// <summary>Calls that failed (502).</summary>
    SignInErrors,

    /// <summary>Calls that ran out of time (504).</summary>
    SignInTimeouts,

    /// <summary>Requests refused with 503: too many calls already running.</summary>
    SignInBusy,

    /// <summary>Requests a rule set refused.</summary>
    RulesDenied,

    /// <summary>Requests a rule set dropped.</summary>
    RulesDropped,

    /// <summary>Requests an allow rule took out of the later rules.</summary>
    RulesAllowed,

    /// <summary>Regex matches that ran past their time limit.</summary>
    RulesTimeouts,
}
