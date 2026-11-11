namespace Paduraksa.Proxy.Metrics;

/// <summary>
/// One subject's append-only <c>typed ID → slot</c> map (PLAN §6.3). The compiler asks it for each object's slot, so an
/// object keeps its counters across every publish; slots of deleted objects are handed out again only after a grace period
/// of one rollup interval.
/// </summary>
/// <remarks>
/// <para>Copies <c>RateLimitPolicySlots</c>: allocation, retirement and reclaim are the compiler's and the publisher's cold
/// path and take a lock; <see cref="IsLive"/>, <see cref="OwnerOf"/> and <see cref="HighWater"/> are lock-free, which is what the
/// sampler and the exporters walk the slots with.</para>
/// <para><b>Owner outlives retirement.</b> A retired slot keeps its last owner until the slot is rented again, so the sampler
/// can still attribute the deleted object's final counts to it. The grace period (many sampler ticks) makes it very unlikely
/// that a count is lost when the counters are cleared for the next owner, but it guarantees nothing about a request that
/// outlives it: that is what the slot's version is for.</para>
/// <para><b>Versions.</b> Every slot has a version, even while stable. A rent makes it odd, clears the counters, sets the owner and
/// makes it even again, so a reader that sees the same even version before and after reading owner and counters read one
/// owner's slot. A compiled <see cref="MetricSlot"/> carries the version it was allocated under; a record whose version is no
/// longer the slot's (the slot was rented to another object while the request was in flight) is dropped, and the sampler
/// and the exporters skip a slot they caught mid-rent.</para>
/// <para><b>Reclaim at a publish.</b> A publish can land out of order: an older candidate that doesn't have an object may swap
/// after a newer one that does, retiring a slot the live snapshot uses. <see cref="Reclaim"/> puts such an object back while
/// its slot is still unrented; if the slot went to another object meanwhile the reclaim is refused and the object gets a
/// fresh slot at the next compile.</para>
/// </remarks>
public sealed class MetricSlotRegistry
{
    /// <summary>Slots a subject can hold.</summary>
    public const int MaxSlots = 65_536;

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, int> _byId = [];
    private readonly List<int> _retired = [];
    private readonly Action<int, bool> _prepare;
    private readonly long _graceMs;
    private readonly int _reserved;
    private readonly int _maxSlots;
    private long[] _reusableAfterMs = new long[16];
    private volatile int[] _versions = new int[16];
    private int _nextSlot;
    private volatile Guid[] _owners = new Guid[16];
    private volatile byte[] _live = new byte[16];

    /// <param name="subject">The kind of object the slots belong to.</param>
    /// <param name="gracePeriod">How long a retired slot is kept before it is rented to another object.</param>
    /// <param name="prepare">Called under the lock for every slot handed out, with whether it is a reused one: makes room for
    /// its counters, and clears them for a reused slot, before any request can be pointed at it.</param>
    /// <param name="reserved">Leading slots that belong to no object and are never retired (the host table's "unmatched" row).</param>
    /// <param name="maxSlots">The most slots, at most <see cref="MaxSlots"/>; a test's way to reach the limit.</param>
    public MetricSlotRegistry(MetricSubject subject, TimeSpan gracePeriod, Action<int, bool> prepare, int reserved = 0, int maxSlots = MaxSlots)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxSlots, MaxSlots);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSlots, reserved);
        _maxSlots = maxSlots;
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentOutOfRangeException.ThrowIfLessThan(gracePeriod, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(reserved);
        Subject = subject;
        _graceMs = (long)gracePeriod.TotalMilliseconds;
        _prepare = prepare;
        _reserved = reserved;
        for (var i = 0; i < reserved; i++)
        {
            Grow(i);
            _live[i] = 1;
            prepare(i, false);
        }

        _nextSlot = reserved;
    }

    public MetricSubject Subject { get; }

    /// <summary>One past the highest slot ever handed out: the sampler's loop bound.</summary>
    public int HighWater => Volatile.Read(ref _nextSlot);

    /// <summary>The object's slot, assigning one if it has none. Compiler path only: takes the lock.</summary>
    /// <exception cref="InvalidOperationException">Every slot is in use and none has finished its grace period.</exception>
    public int GetOrAdd(Guid id, long nowMs)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(id, out var existing))
            {
                return existing;
            }

            // A slot that finished its grace period is reused before the table grows: memory follows the objects in use, not
            // the objects ever created.
            var rented = TryRentRetired(nowMs, out var slot);
            if (!rented)
            {
                if (_nextSlot >= _maxSlots)
                {
                    throw new InvalidOperationException(
                        $"All {_maxSlots} {Subject.ToString().ToLowerInvariant()} metric slots are in use and no retired slot has finished its grace period yet.");
                }

                slot = _nextSlot;
                Grow(slot);
                _prepare(slot, false);
                Volatile.Write(ref _nextSlot, slot + 1);
            }

            _byId.Add(id, slot);
            _owners[slot] = id;
            Volatile.Write(ref _live[slot], 1);
            if (rented)
            {
                // Even again: the slot belongs to its new owner, counters cleared.
                Interlocked.Increment(ref _versions[slot]);
            }

            return slot;
        }
    }

    /// <summary>The object's slot without allocating one: how a compile that will never be published looks an object up.</summary>
    public bool TryGetSlot(Guid id, out int slot)
    {
        lock (_gate)
        {
            return _byId.TryGetValue(id, out slot);
        }
    }

    /// <summary>
    /// Re-asserts at a publish that <paramref name="id"/> owns <paramref name="slot"/>, undoing a retirement a different
    /// publish slipped in. True when the object is live on that slot again (or already was).
    /// </summary>
    public bool Reclaim(Guid id, int slot)
    {
        if (slot < _reserved || slot >= HighWater)
        {
            return false;
        }

        lock (_gate)
        {
            if (_byId.TryGetValue(id, out var existing))
            {
                if (existing != slot)
                {
                    return false;
                }

                Volatile.Write(ref _live[slot], 1);
                return true;
            }

            if (Volatile.Read(ref _live[slot]) != 0 || _owners[slot] != id || !_retired.Remove(slot))
            {
                return false;
            }

            _byId.Add(id, slot);
            Volatile.Write(ref _live[slot], 1);
            return true;
        }
    }

    /// <summary>The object's slot stops being live; it is handed out again after the grace period. False when it had none.</summary>
    public bool Retire(Guid id, long nowMs)
    {
        lock (_gate)
        {
            if (!_byId.Remove(id, out var slot))
            {
                return false;
            }

            Volatile.Write(ref _live[slot], 0);
            _reusableAfterMs[slot] = nowMs + _graceMs;
            _retired.Add(slot);
            return true;
        }
    }

    /// <summary>
    /// Makes <paramref name="entries"/> the live set: re-asserts each object's slot, then retires every other live object, so
    /// a slot a compile took for a candidate that never went live is reclaimed at the next publish too. The objects whose
    /// slot another object now holds are returned (they compile onto a fresh slot next time).
    /// </summary>
    public int Publish(ReadOnlySpan<KeyValuePair<Guid, int>> entries, long nowMs)
    {
        var conflicts = 0;
        HashSet<Guid> keep = [];
        foreach (var (id, slot) in entries)
        {
            keep.Add(id);
            if (!Reclaim(id, slot))
            {
                conflicts++;
            }
        }

        List<Guid> gone = [];
        lock (_gate)
        {
            foreach (var id in _byId.Keys)
            {
                if (!keep.Contains(id))
                {
                    gone.Add(id);
                }
            }
        }

        foreach (var id in gone)
        {
            Retire(id, nowMs);
        }

        return conflicts;
    }

    /// <summary>The slot's version: even when stable, odd while it is being rented. A slot compiled into a snapshot carries it. Lock-free.</summary>
    public int VersionOf(int slot)
    {
        var versions = _versions;
        return (uint)slot < (uint)versions.Length ? Volatile.Read(ref versions[slot]) : 0;
    }

    /// <summary>Whether <paramref name="slot"/> still belongs to the owner that was given <paramref name="version"/>. One compare, lock-free.</summary>
    public bool IsCurrent(int slot, int version)
    {
        var versions = _versions;
        return (uint)slot < (uint)versions.Length && Volatile.Read(ref versions[slot]) == version;
    }

    /// <summary>Whether the slot stands for an object right now. Lock-free.</summary>
    public bool IsLive(int slot)
    {
        var live = _live;
        return (uint)slot < (uint)live.Length && Volatile.Read(ref live[slot]) != 0;
    }

    /// <summary>The slot's last owner (still the owner while retired), or <see cref="Guid.Empty"/> for a reserved or unused slot. Lock-free.</summary>
    public Guid OwnerOf(int slot)
    {
        var owners = _owners;
        return (uint)slot < (uint)owners.Length ? owners[slot] : Guid.Empty;
    }

    private bool TryRentRetired(long nowMs, out int slot)
    {
        for (var i = 0; i < _retired.Count; i++)
        {
            var candidate = _retired[i];
            if (_reusableAfterMs[candidate] > nowMs)
            {
                continue;
            }

            _retired.RemoveAt(i);
            // Odd while the counters are cleared and the owner changes: nobody records into or reads the slot meanwhile.
            Interlocked.Increment(ref _versions[candidate]);
            _prepare(candidate, true);
            slot = candidate;
            return true;
        }

        slot = -1;
        return false;
    }

    // The arrays are replaced (never resized in place) so lock-free readers always see a complete one.
    private void Grow(int slot)
    {
        if (slot < _owners.Length)
        {
            return;
        }

        var size = Math.Min(Math.Max(_owners.Length * 2, slot + 1), _maxSlots);
        Array.Resize(ref _reusableAfterMs, size);
        var versions = new int[size];
        Array.Copy(_versions, versions, _versions.Length);
        _versions = versions;
        var owners = new Guid[size];
        Array.Copy(_owners, owners, _owners.Length);
        var live = new byte[size];
        Array.Copy(_live, live, _live.Length);
        _owners = owners;
        _live = live;
    }
}
