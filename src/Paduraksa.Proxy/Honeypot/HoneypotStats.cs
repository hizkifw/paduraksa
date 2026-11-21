namespace Paduraksa.Proxy.Honeypot;

/// <summary>Process-lifetime trap counters, lock-free and read through <see cref="Volatile"/>.</summary>
public sealed class HoneypotStats
{
    private long _served;
    private long _shed;
    private long _dropped;

    /// <summary>Trap answers written, the challenge page and the verify answers included.</summary>
    public long Served => Volatile.Read(ref _served);

    /// <summary>Requests answered <c>503</c> because the trap was already generating its cap of answers.</summary>
    public long Shed => Volatile.Read(ref _shed);

    /// <summary>Hits the recording side had no room for (the sink's bounded queue was full).</summary>
    public long Dropped => Volatile.Read(ref _dropped);

    internal void CountServed() => Interlocked.Increment(ref _served);

    internal void CountShed() => Interlocked.Increment(ref _shed);

    /// <summary>For the sink to call when it discards a hit.</summary>
    public void CountDropped() => Interlocked.Increment(ref _dropped);
}
