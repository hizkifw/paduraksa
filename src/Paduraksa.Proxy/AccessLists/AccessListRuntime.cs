using System.Security.Cryptography;

using Paduraksa.Proxy.Net;

namespace Paduraksa.Proxy.AccessLists;

/// <summary>
/// What the access-list policies need at request time from outside the immutable snapshot: the verification cache, its
/// per-process key, the clock and the counters. One per process, handed to the compiler like the rate-limit runtime.
/// </summary>
public sealed class AccessListRuntime
{
    private readonly long _start;

    /// <summary>How many PBKDF2 checks one client may have queued and running together.</summary>
    public const int DefaultMaxPerClient = 4;

    /// <summary>How long a request may wait for a verification slot before it is refused.</summary>
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(10);

    internal const int MinConcurrentVerifications = 8;

    /// <summary>The line holds this many requests per slot by default: at tens of milliseconds a check, the last one in a full
    /// line waits a few seconds, well inside <see cref="DefaultMaxWait"/> whatever the machine's size.</summary>
    internal const int QueuedPerSlot = 32;

    public AccessListRuntime()
        : this(TimeProvider.System)
    {
    }

    public AccessListRuntime(
        TimeProvider time,
        int cacheCapacity = AccessListVerificationCache.DefaultCapacity,
        int? maxConcurrentVerifications = null,
        int maxVerificationsPerClient = DefaultMaxPerClient,
        int? maxQueued = null,
        TimeSpan? maxWait = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        Time = time;
        Cache = new AccessListVerificationCache(cacheCapacity);
        _start = time.GetTimestamp();

        // PBKDF2 runs on a thread-pool thread, so a flood of wrong passwords could otherwise take every thread the pool has.
        // The floor is for small machines: with a cap of 2 a page's third credentialed request on a cold cache got no slot.
        // Over-subscribing a 2-core box with PBKDF2 only makes each check slower, which is the cheaper failure.
        var slots = maxConcurrentVerifications ?? Math.Max(MinConcurrentVerifications, Environment.ProcessorCount);
        Queue = new AccessListVerificationQueue(time, Stats, slots, maxQueued ?? slots * QueuedPerSlot, maxVerificationsPerClient, maxWait ?? DefaultMaxWait);
        Key = RandomNumberGenerator.GetBytes(32);
    }

    /// <summary>The runtime a compile uses when none is configured: a cache of its own that nothing else reads.</summary>
    public static AccessListRuntime Default { get; } = new();

    public TimeProvider Time { get; }

    public AccessListVerificationCache Cache { get; }

    public AccessListStats Stats { get; } = new();

    /// <summary>Drawn when the process starts: a client can't compute a fingerprint, so it can't aim one at another's entry.</summary>
    internal byte[] Key { get; }

    internal AccessListVerificationQueue Queue { get; }

    internal long NowMs => (long)Time.GetElapsedTime(_start).TotalMilliseconds;
}

/// <summary>Process-lifetime access-list counters; the security page reads them. Each count is one interlocked add.</summary>
public sealed class AccessListStats
{
    private long _allowed;
    private long _addressRefused;
    private long _unauthorized;
    private long _cacheHits;
    private long _verifications;
    private long _busy;
    private long _queued;
    private long _queueFull;
    private long _queueTimeouts;

    /// <summary>Requests an access list let through.</summary>
    public long Allowed => Volatile.Read(ref _allowed);

    /// <summary>Requests refused for their address (403 or dropped).</summary>
    public long AddressRefused => Volatile.Read(ref _addressRefused);

    /// <summary>Requests answered 401: no credentials, or wrong ones.</summary>
    public long Unauthorized => Volatile.Read(ref _unauthorized);

    /// <summary>Credentials accepted from the verification cache, without PBKDF2.</summary>
    public long CacheHits => Volatile.Read(ref _cacheHits);

    /// <summary>PBKDF2 checks run.</summary>
    public long Verifications => Volatile.Read(ref _verifications);

    /// <summary>Requests answered 429 because no PBKDF2 slot came free for them: <see cref="QueueFull"/> plus <see cref="QueueTimeouts"/>.</summary>
    public long Busy => Volatile.Read(ref _busy);

    /// <summary>Requests that waited in the line for a PBKDF2 slot (however the wait ended).</summary>
    public long Queued => Volatile.Read(ref _queued);

    /// <summary>Requests refused at once because the line was full or their client already held its share of it.</summary>
    public long QueueFull => Volatile.Read(ref _queueFull);

    /// <summary>Requests that waited the maximum and were refused.</summary>
    public long QueueTimeouts => Volatile.Read(ref _queueTimeouts);

    internal void CountAllowed() => Interlocked.Increment(ref _allowed);

    internal void CountAddressRefused() => Interlocked.Increment(ref _addressRefused);

    internal void CountUnauthorized() => Interlocked.Increment(ref _unauthorized);

    internal void CountCacheHit() => Interlocked.Increment(ref _cacheHits);

    internal void CountVerification() => Interlocked.Increment(ref _verifications);

    internal void CountQueued() => Interlocked.Increment(ref _queued);

    internal void CountQueueFull()
    {
        Interlocked.Increment(ref _queueFull);
        Interlocked.Increment(ref _busy);
    }

    internal void CountQueueTimeout()
    {
        Interlocked.Increment(ref _queueTimeouts);
        Interlocked.Increment(ref _busy);
    }
}
