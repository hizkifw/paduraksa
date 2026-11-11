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
    private readonly int _maxVerifications;
    private readonly int _maxPerClient;
    private readonly int _pressureAt;
    private readonly int[] _perClient = new int[ClientStripes];
    private readonly ulong _stripeSeed = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
    private int _verifying;

    /// <summary>Stripes of the per-client in-flight counters (4 KiB): a client maps to one by a keyed hash of its masked
    /// address, so two clients share a stripe only by chance, and nobody can aim at another's.</summary>
    private const int ClientStripes = 1024;

    /// <summary>How many PBKDF2 checks one client may have running at once once half the global cap is in use.</summary>
    public const int DefaultMaxPerClient = 2;

    public AccessListRuntime()
        : this(TimeProvider.System)
    {
    }

    public AccessListRuntime(TimeProvider time, int cacheCapacity = AccessListVerificationCache.DefaultCapacity, int? maxConcurrentVerifications = null, int maxVerificationsPerClient = DefaultMaxPerClient)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxVerificationsPerClient, 1);
        _maxPerClient = maxVerificationsPerClient;
        ArgumentNullException.ThrowIfNull(time);
        Time = time;
        Cache = new AccessListVerificationCache(cacheCapacity);
        _start = time.GetTimestamp();

        // PBKDF2 runs on the request's thread, so a flood of wrong passwords could otherwise take every thread the pool has. The
        // global limit is the outer bound. Idle, any client may use it all (an app reopening after the cache expired sends
        // several credentialed requests at once); once half is taken, each client's own share (a masked address, as the cache
        // keys it) keeps one sprayer from taking the rest, so it only eats its own 429s.
        _maxVerifications = maxConcurrentVerifications ?? Math.Max(2, Environment.ProcessorCount);
        _pressureAt = Math.Max(1, _maxVerifications / 2);
        Key = RandomNumberGenerator.GetBytes(32);
    }

    /// <summary>The runtime a compile uses when none is configured: a cache of its own that nothing else reads.</summary>
    public static AccessListRuntime Default { get; } = new();

    public TimeProvider Time { get; }

    public AccessListVerificationCache Cache { get; }

    public AccessListStats Stats { get; } = new();

    /// <summary>Drawn when the process starts: a client can't compute a fingerprint, so it can't aim one at another's entry.</summary>
    internal byte[] Key { get; }

    internal long NowMs => (long)Time.GetElapsedTime(_start).TotalMilliseconds;

    /// <summary>Takes a verification slot for <paramref name="client"/>. The global cap is the hard bound. While fewer than
    /// half of it is in use anyone is admitted, so a page that fires several credentialed requests at once on a cold cache
    /// is not refused; from half up, a client may hold at most its own share. 0 B, lock-free. Every true must be paired with
    /// <see cref="ExitVerification"/> with the same client, in a <c>finally</c>.</summary>
    internal bool TryEnterVerification(IpKey client)
    {
        var running = Interlocked.Increment(ref _verifying);
        if (running > _maxVerifications)
        {
            Interlocked.Decrement(ref _verifying);
            return false;
        }

        // The client's count is kept whatever the load, so the share is right the moment the load rises.
        ref var own = ref _perClient[Stripe(client)];
        if (Interlocked.Increment(ref own) > _maxPerClient && running > _pressureAt)
        {
            Interlocked.Decrement(ref own);
            Interlocked.Decrement(ref _verifying);
            return false;
        }

        return true;
    }

    internal void ExitVerification(IpKey client)
    {
        Interlocked.Decrement(ref _verifying);
        Interlocked.Decrement(ref _perClient[Stripe(client)]);
    }

    private int Stripe(IpKey client)
    {
        var value = client.MaskTo(32, 64).Value;
        var hash = ((ulong)value ^ ((ulong)(value >> 64) * 0x9E37_79B9_7F4A_7C15UL) ^ _stripeSeed) * 0xFF51_AFD7_ED55_8CCDUL;
        return (int)((hash ^ (hash >> 32)) & (ClientStripes - 1));
    }
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

    /// <summary>Requests answered 429 because too many PBKDF2 checks were already running.</summary>
    public long Busy => Volatile.Read(ref _busy);

    internal void CountAllowed() => Interlocked.Increment(ref _allowed);

    internal void CountAddressRefused() => Interlocked.Increment(ref _addressRefused);

    internal void CountUnauthorized() => Interlocked.Increment(ref _unauthorized);

    internal void CountCacheHit() => Interlocked.Increment(ref _cacheHits);

    internal void CountVerification() => Interlocked.Increment(ref _verifications);

    internal void CountBusy() => Interlocked.Increment(ref _busy);
}
