namespace Paduraksa.Proxy.AccessLists;

/// <summary>
/// Remembers that a credential has just passed PBKDF2, so the next request with the same one doesn't pay for it again.
/// A fixed table of two arrays, lock-free and bounded: a flood of distinct credentials can only evict one another.
/// </summary>
/// <remarks>
/// <para><b>What is stored.</b> A 64-bit fingerprint (a keyed SHA-256 of the list, user, password and masked client IP;
/// see <see cref="AccessListPolicy"/>) and an expiry in milliseconds. Never a password, a hash or an address. Only a
/// <i>successful</i> verification is ever added, so a miss costs the attacker a PBKDF2 and a hit proves a real one ran.</para>
/// <para><b>The race.</b> A writer clears the expiry, stores the key, then stores the new expiry; a reader reads the key,
/// the expiry, then the key again and believes it only when both reads of the key agree and the expiry is live. So a
/// reader never pairs one entry's key with another's expiry. Two writers on one slot can leave a key with the other's
/// expiry, which is harmless: each only writes a key it has just verified, with an expiry of the same moment.</para>
/// </remarks>
public sealed class AccessListVerificationCache
{
    /// <summary>How long a verified credential stays trusted.</summary>
    public const long LifetimeMs = 5 * 60 * 1000;

    /// <summary>The default entry count: 64 KiB.</summary>
    public const int DefaultCapacity = 4096;

    private const int Probes = 4;

    private readonly ulong[] _keys;
    private readonly long[] _expires;
    private readonly int _mask;

    public AccessListVerificationCache(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, Probes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 1 << 24);
        if ((capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The capacity must be a power of two.");
        }

        _keys = new ulong[capacity];
        _expires = new long[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _keys.Length;

    /// <summary>Whether <paramref name="key"/> was added and has not expired. 0 B.</summary>
    public bool Contains(ulong key, long nowMs)
    {
        var start = (int)key & _mask;
        for (var i = 0; i < Probes; i++)
        {
            var slot = (start + i) & _mask;
            if (Volatile.Read(ref _keys[slot]) != key)
            {
                continue;
            }

            var expires = Volatile.Read(ref _expires[slot]);
            if (expires > nowMs && Volatile.Read(ref _keys[slot]) == key)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Records a verified credential until <see cref="LifetimeMs"/> from <paramref name="nowMs"/>.</summary>
    public void Add(ulong key, long nowMs)
    {
        var start = (int)key & _mask;
        var chosen = -1;
        var oldest = long.MaxValue;
        for (var i = 0; i < Probes; i++)
        {
            var slot = (start + i) & _mask;
            if (Volatile.Read(ref _keys[slot]) == key)
            {
                chosen = slot;
                break;
            }

            // An expired or never-used slot is free; otherwise the one that would lapse first is the one to give up.
            var expires = Volatile.Read(ref _expires[slot]);
            var rank = expires <= nowMs ? long.MinValue : expires;
            if (rank < oldest)
            {
                oldest = rank;
                chosen = slot;
            }
        }

        Volatile.Write(ref _expires[chosen], 0);
        Volatile.Write(ref _keys[chosen], key);
        Volatile.Write(ref _expires[chosen], nowMs + LifetimeMs);
    }

    /// <summary>The entries that haven't expired (a scan, for tests and diagnostics).</summary>
    public int LiveCount(long nowMs)
    {
        var live = 0;
        for (var i = 0; i < _keys.Length; i++)
        {
            if (Volatile.Read(ref _expires[i]) > nowMs)
            {
                live++;
            }
        }

        return live;
    }
}
