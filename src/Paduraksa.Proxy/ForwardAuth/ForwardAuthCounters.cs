namespace Paduraksa.Proxy.ForwardAuth;

/// <summary>What one auth provider has decided since the process started. Lock-free counters the security overview reads; they survive a
/// publish, because a provider keeps its ID (the decision cache, which depends on the provider's configuration, doesn't: see
/// <see cref="ForwardAuthProviderState"/>).</summary>
public sealed class ForwardAuthCounters
{
    private long _allowed;
    private long _denied;
    private long _redirected;
    private long _cacheHits;
    private long _calls;
    private long _errors;
    private long _timeouts;
    private long _busy;

    /// <summary>Requests the provider let through, from its answer or from the decision cache.</summary>
    public long Allowed => Interlocked.Read(ref _allowed);

    /// <summary>Requests the provider refused with a 401, 403 or 407 that was relayed to the client.</summary>
    public long Denied => Interlocked.Read(ref _denied);

    /// <summary>Requests sent to a sign-in page: the provider's own redirect, relayed, or the configured sign-in URL.</summary>
    public long Redirected => Interlocked.Read(ref _redirected);

    /// <summary>Allows answered from the decision cache, with no call.</summary>
    public long CacheHits => Interlocked.Read(ref _cacheHits);

    /// <summary>Calls made to the provider's endpoint.</summary>
    public long Calls => Interlocked.Read(ref _calls);

    /// <summary>Calls that failed (the endpoint was unreachable or answered something it must not) and refused the request with 502.</summary>
    public long Errors => Interlocked.Read(ref _errors);

    /// <summary>Calls that ran out of time and refused the request with 504.</summary>
    public long Timeouts => Interlocked.Read(ref _timeouts);

    /// <summary>Requests refused with 503 because too many calls were already running.</summary>
    public long Busy => Interlocked.Read(ref _busy);

    internal void CountAllowed() => Interlocked.Increment(ref _allowed);

    internal void CountDenied() => Interlocked.Increment(ref _denied);

    internal void CountRedirected() => Interlocked.Increment(ref _redirected);

    internal void CountCacheHit() => Interlocked.Increment(ref _cacheHits);

    internal void CountCall() => Interlocked.Increment(ref _calls);

    internal void CountError() => Interlocked.Increment(ref _errors);

    internal void CountTimeout() => Interlocked.Increment(ref _timeouts);

    internal void CountBusy() => Interlocked.Increment(ref _busy);
}
