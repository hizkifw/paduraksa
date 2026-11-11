namespace Paduraksa.Proxy.RateLimiting;

/// <summary>
/// Process-lifetime rate-limit counters, lock-free and read through <see cref="Volatile"/>: the security overview reads
/// them. A count is one <see cref="System.Threading.Interlocked.Increment(ref long)"/> on the request path and
/// allocates nothing.
/// </summary>
public sealed class RateLimitStats
{
    private long _allowed;
    private long _limited;

    /// <summary>Requests a rate-limit policy let through with a token.</summary>
    public long Allowed => Volatile.Read(ref _allowed);

    /// <summary>Requests a rate-limit policy refused with 429.</summary>
    public long Limited => Volatile.Read(ref _limited);

    internal void CountAllowed() => Interlocked.Increment(ref _allowed);

    internal void CountLimited() => Interlocked.Increment(ref _limited);
}
