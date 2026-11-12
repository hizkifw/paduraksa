using Paduraksa.Proxy.RateLimiting;

namespace Paduraksa.Proxy.Net;

/// <summary>What <see cref="ConnectionTable.TryAcquire"/> did.</summary>
public enum ConnectionSlot : byte
{
    /// <summary>The connection is counted: <see cref="ConnectionTable.Release"/> must follow when it closes.</summary>
    Acquired = 0,

    /// <summary>The address already holds the cap: the connection is refused and nothing was counted.</summary>
    OverCap,

    /// <summary>No entry was free for this address, so the connection is let through uncounted (fail open): there is
    /// nothing to release.</summary>
    Overflow,
}

/// <summary>
/// The open connections per client address: a fixed-size, open-addressed, lock-free table of counters, one word per entry,
/// so a call allocates nothing and never blocks (PLAN §6.3, §7.2 A). It is process state and not snapshot state: a publish
/// changes the cap a connection is judged against and nothing else.
/// </summary>
/// <remarks>
/// <para><b>Entries.</b> One <see cref="ulong"/> each: bits 63-24 are the key's tag (40 bits of its hash, never 0), bits
/// 23-0 the open connections of that key. 0 is an empty entry. The key is <see cref="RateLimitPartition.ForIp"/>: the whole
/// IPv4 address or the IPv6 /64, keyed by a per-process random seed, so a client can't work out which addresses collide with
/// its own. A tag collision merges two addresses' counts, which only makes the cap stricter.</para>
/// <para><b>Probing.</b> A key is looked for in <see cref="ProbeLength"/> consecutive entries from the low bits of its hash.
/// Not found: it claims the first empty entry, or else an entry that holds no connection (a count of 0 keeps its tag, so a
/// busy address doesn't lose its entry between two connections), by compare-and-swap of the whole word. An entry that
/// holds a connection is never taken, so a counted connection's entry stays put until it is released.</para>
/// <para><b>Overflow fails open.</b> When every entry of the window holds an open connection of another key, the
/// connection is let through uncounted (<see cref="ConnectionSlot.Overflow"/>, counted by the caller): a flood from many
/// addresses can fill the table and then no new address is capped, which is what Kestrel's own limits give without this
/// table. The table's size bounds the memory whatever the traffic.</para>
/// <para><b>Accepted race.</b> Two threads that both fail to find a new key and claim different entries (one empty, one
/// that went idle in between) give the key two entries, so for that moment it may hold up to twice the cap; a release
/// takes a connection from whichever entry of its tag holds one, so the counts stay balanced.</para>
/// </remarks>
public sealed class ConnectionTable
{
    /// <summary>Entries in a table built without a size: 64 Ki, 512 KiB.</summary>
    public const int DefaultCapacity = 65_536;

    /// <summary>Entries a key's probe looks at.</summary>
    public const int ProbeLength = 8;

    /// <summary>The most connections one entry counts (24 bits); at it an address is treated as over its cap.</summary>
    public const int MaxCount = (1 << 24) - 1;

    private const int TagShift = 24;
    private const ulong CountMask = (1UL << TagShift) - 1;

    // A probe starts over when it lost the entry it was going to claim, which is a change in its own window; four losses
    // in a row mean the window keeps moving, and the connection goes through uncounted rather than waiting.
    private const int MaxAttempts = 4;

    private readonly ulong[] _entries;
    private readonly int _mask;

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> isn't a power of two from
    /// <see cref="ProbeLength"/> to 16 Mi.</exception>
    public ConnectionTable(int capacity = DefaultCapacity)
    {
        if (capacity < ProbeLength || capacity > 16 * 1_024 * 1_024 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity), capacity, $"The connection table size must be a power of two from {ProbeLength} to 16777216.");
        }

        _entries = new ulong[capacity];
        _mask = capacity - 1;
        Capacity = capacity;
    }

    public int Capacity { get; }

    /// <summary>
    /// Counts one more connection for <paramref name="hash"/> (a <see cref="RateLimitPartition.ForIp"/> hash) unless the
    /// address already holds <paramref name="cap"/>. <see cref="ConnectionSlot.Acquired"/> is the only result that needs a
    /// <see cref="Release"/>.
    /// </summary>
    public ConnectionSlot TryAcquire(ulong hash, int cap)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cap);
        var tag = TagOf(hash);
        var start = (int)(hash & (ulong)_mask);
        var limit = (ulong)Math.Min(cap, MaxCount);

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var spare = -1;
            ulong spareWord = 0;
            var lost = false;
            for (var i = 0; i < ProbeLength; i++)
            {
                var index = (start + i) & _mask;
                var word = Volatile.Read(ref _entries[index]);
                if (word >> TagShift == tag)
                {
                    var result = TryIncrement(index, word, tag, limit, out var moved);
                    if (moved)
                    {
                        lost = true;
                        break;
                    }

                    return result;
                }

                if (spare < 0 && (word == 0 || (word & CountMask) == 0))
                {
                    spare = index;
                    spareWord = word;
                }
            }

            if (lost)
            {
                continue;
            }

            if (spare < 0)
            {
                return ConnectionSlot.Overflow;
            }

            if (Interlocked.CompareExchange(ref _entries[spare], (tag << TagShift) | 1, spareWord) == spareWord)
            {
                return ConnectionSlot.Acquired;
            }
        }

        return ConnectionSlot.Overflow;
    }

    /// <summary>The connections <paramref name="hash"/> holds now, for the cap check that doesn't count (QUIC's handshake).</summary>
    public int Count(ulong hash)
    {
        var tag = TagOf(hash);
        var start = (int)(hash & (ulong)_mask);
        var total = 0UL;
        for (var i = 0; i < ProbeLength; i++)
        {
            var word = Volatile.Read(ref _entries[(start + i) & _mask]);
            if (word >> TagShift == tag)
            {
                total += word & CountMask;
            }
        }

        return (int)Math.Min(total, int.MaxValue);
    }

    /// <summary>Gives back a connection <see cref="TryAcquire"/> counted.</summary>
    public void Release(ulong hash)
    {
        var tag = TagOf(hash);
        var start = (int)(hash & (ulong)_mask);
        for (var i = 0; i < ProbeLength; i++)
        {
            var index = (start + i) & _mask;
            var word = Volatile.Read(ref _entries[index]);
            while (word >> TagShift == tag && (word & CountMask) != 0)
            {
                var seen = Interlocked.CompareExchange(ref _entries[index], word - 1, word);
                if (seen == word)
                {
                    return;
                }

                word = seen;
            }
        }
    }

    /// <summary>Connections counted across the table (a sum of every entry), for tests and diagnostics: it reads the whole table.</summary>
    public long TotalCount()
    {
        long total = 0;
        for (var i = 0; i < _entries.Length; i++)
        {
            total += (long)(Volatile.Read(ref _entries[i]) & CountMask);
        }

        return total;
    }

    private ConnectionSlot TryIncrement(int index, ulong word, ulong tag, ulong limit, out bool moved)
    {
        moved = false;
        while (true)
        {
            if ((word & CountMask) >= limit)
            {
                return ConnectionSlot.OverCap;
            }

            var seen = Interlocked.CompareExchange(ref _entries[index], word + 1, word);
            if (seen == word)
            {
                return ConnectionSlot.Acquired;
            }

            word = seen;
            if (word >> TagShift != tag)
            {
                // The entry was taken over for another key while this thread held it idle: probe again from the start.
                moved = true;
                return ConnectionSlot.Overflow;
            }
        }
    }

    private static ulong TagOf(ulong hash)
    {
        var tag = hash >> 8;
        return tag == 0 ? 1 : tag;
    }
}
