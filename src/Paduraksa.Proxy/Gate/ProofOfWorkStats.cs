namespace Paduraksa.Proxy.Gate;

/// <summary>
/// Process-lifetime proof-of-work counters, lock-free and read through <see cref="Volatile"/>: the security overview reads them.
/// Nothing here is on the decision's allocation path.
/// </summary>
public sealed class ProofOfWorkStats
{
    private long _challengesServed;
    private long _solved;
    private long _malformed;
    private long _badTag;
    private long _expired;
    private long _tooEasy;
    private long _blockedNonBrowser;
    private long _verifyRejected;

    /// <summary>Challenge pages written (browser requests without a valid token).</summary>
    public long ChallengesServed => Volatile.Read(ref _challengesServed);

    /// <summary>Verify requests that produced a cookie.</summary>
    public long Solved => Volatile.Read(ref _solved);

    /// <summary>Cookies that didn't parse as a token this build issues (including duplicates and bad base64url).</summary>
    public long Malformed => Volatile.Read(ref _malformed);

    /// <summary>Tokens whose tag didn't verify: altered, or issued for another host or client address.</summary>
    public long BadTag => Volatile.Read(ref _badTag);

    /// <summary>Tokens that verified but had expired.</summary>
    public long Expired => Volatile.Read(ref _expired);

    /// <summary>Tokens that verified but were easier than the host's current difficulty.</summary>
    public long TooEasy => Volatile.Read(ref _tooEasy);

    /// <summary>403 answers to a request without a token whose method or <c>Accept</c> isn't a browser's.</summary>
    public long BlockedNonBrowser => Volatile.Read(ref _blockedNonBrowser);

    /// <summary>Verify requests answered 403: a bad challenge or solution, or a token the verify endpoint wouldn't mint.</summary>
    public long VerifyRejected => Volatile.Read(ref _verifyRejected);

    internal void CountChallengeServed() => Interlocked.Increment(ref _challengesServed);

    internal void CountSolved() => Interlocked.Increment(ref _solved);

    internal void CountMalformed() => Interlocked.Increment(ref _malformed);

    internal void CountBadTag() => Interlocked.Increment(ref _badTag);

    internal void CountExpired() => Interlocked.Increment(ref _expired);

    internal void CountTooEasy() => Interlocked.Increment(ref _tooEasy);

    internal void CountBlockedNonBrowser() => Interlocked.Increment(ref _blockedNonBrowser);

    internal void CountVerifyRejected() => Interlocked.Increment(ref _verifyRejected);
}
