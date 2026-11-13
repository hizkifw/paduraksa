namespace Paduraksa.Proxy.RateLimiting;

/// <summary>
/// Maps a rate-limit policy's stable identity to its 16-bit table slot: append-only, so a bucket survives every publish
/// of a config that still has the policy (docs/design/config.md "State that survives a publish"). Slot 0 is reserved for "no policy"; the other 65 535 are handed
/// out once and reused only after their policy has been gone for the grace period.
/// </summary>
/// <remarks>
/// <para><b>Why slots at all.</b> The table's key holds the slot, so a bucket belongs to one policy for as long as the
/// slot does. Keying by a snapshot's array position would move every policy's buckets on any edit, resetting the limits.</para>
/// <para><b>Retire and reuse.</b> Removing a policy from the config makes its slot non-live at once, so the sweeper clears
/// its buckets. The slot itself is not handed out again until the grace period has passed, so a snapshot still being
/// served from the old config (a publish in flight) cannot spend a new policy's tokens; when it is handed out, its
/// buckets are cleared first, best effort behind the sweeper's passes (which clear a non-live slot from the first pass after
/// the retirement): only a bucket an old-snapshot request keeps moving through every clear attempt could survive.</para>
/// <para><b>Threads.</b> Allocation and retirement are the compiler's cold path and take a lock. Liveness
/// (<see cref="IsLive"/>) and <see cref="Parameters"/> are lock-free reads, which is what the sweeper walks the table
/// with: a sweep that runs across a publish sees either side of it, which only delays or brings a clear forward by one
/// pass.</para>
/// <para><b>Reclaim at a publish.</b> A publish can land out of order: an older candidate that removes a pair may swap
/// after a newer one re-added it, retiring a slot the live snapshot still uses. <see cref="Reclaim"/> puts such a pair
/// back (mapping and live flag) while the slot is still retired inside its grace period. If the slot was rented to
/// another identity in the meantime, the reclaim is refused: the pair keeps its bucket only until the next compile gives
/// it a fresh slot, rather than silently sharing another policy's tokens.</para>
/// </remarks>
public sealed class RateLimitPolicySlots
{
    /// <summary>Slots a policy can hold (1 to 65 535; 0 is "no policy").</summary>
    public const int MaxSlots = 65_535;

    /// <summary>One rollup's worth of time, like the metric slots: long enough to outlive a publish in flight.</summary>
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, ushort> _byIdentity = new(StringComparer.Ordinal);
    private readonly byte[] _live = new byte[MaxSlots + 1];
    private readonly long[] _reusableAfterMs = new long[MaxSlots + 1];
    private readonly List<ushort> _retired = [];
    private readonly RateLimitTable _table;
    private readonly long _graceMs;

    private RateLimitPolicyParameters _parameters = RateLimitPolicyParameters.Empty;
    private int _nextSlot = 1;

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="gracePeriod"/> is negative.</exception>
    public RateLimitPolicySlots(RateLimitTable table, TimeSpan? gracePeriod = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        var grace = gracePeriod ?? DefaultGracePeriod;
        ArgumentOutOfRangeException.ThrowIfLessThan(grace, TimeSpan.Zero);

        _table = table;
        _graceMs = (long)grace.TotalMilliseconds;
    }

    /// <summary>The live policies' numbers, published by the compiler. One atomic read, never a half-updated set.</summary>
    public RateLimitPolicyParameters Parameters => Volatile.Read(ref _parameters);

    public void Publish(RateLimitPolicyParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        Volatile.Write(ref _parameters, parameters);
    }

    /// <summary>The identity's slot, assigning it one if it has none. Compiler path only: takes the lock.</summary>
    /// <exception cref="InvalidOperationException">All 65 535 slots are in use and none has finished its grace period.</exception>
    public ushort GetOrAdd(string identity, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(identity);

        lock (_gate)
        {
            if (_byIdentity.TryGetValue(identity, out var existing))
            {
                return existing;
            }

            var slot = _nextSlot <= MaxSlots ? (ushort)_nextSlot++ : RentRetired(nowMs);
            _byIdentity.Add(identity, slot);
            Volatile.Write(ref _live[slot], 1);
            return slot;
        }
    }

    /// <summary>The identity's slot without allocating one: how a compile that will never be published looks a pair up.</summary>
    public bool TryGetSlot(string identity, out ushort slot)
    {
        ArgumentNullException.ThrowIfNull(identity);

        lock (_gate)
        {
            return _byIdentity.TryGetValue(identity, out slot);
        }
    }

    /// <summary>
    /// Re-asserts at a publish that <paramref name="identity"/> owns <paramref name="slot"/>, undoing a retirement an
    /// older publish slipped in between the compile and this swap. True when the pair is live again (or already was);
    /// false when the slot has meanwhile been rented to another identity, in which case the pair must not share its
    /// buckets and starts on a fresh slot at the next compile.
    /// </summary>
    /// <remarks>
    /// It only revives a slot that is still unrented: still in the retired list. The grace period would normally
    /// guarantee that for a minute, but a late publish may arrive after it, and an unrented slot is safe to take back
    /// whenever. A live slot the registry doesn't map to this identity belongs to someone else.
    /// </remarks>
    public bool Reclaim(string identity, ushort slot, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (slot == 0 || slot > MaxSlots)
        {
            return false;
        }

        lock (_gate)
        {
            if (_byIdentity.TryGetValue(identity, out var existing))
            {
                if (existing != slot)
                {
                    return false;
                }

                Volatile.Write(ref _live[slot], 1);
                return true;
            }

            if (Volatile.Read(ref _live[slot]) != 0)
            {
                return false;
            }

            // The grace period is the window this is safe for: the slot may be rented once it has passed.
            if (_reusableAfterMs[slot] <= nowMs || !_retired.Remove(slot))
            {
                return false;
            }

            _byIdentity.Add(identity, slot);
            Volatile.Write(ref _live[slot], 1);
            return true;
        }
    }

    /// <summary>
    /// The identity's slot stops being live: the sweeper clears its buckets now, and the slot is handed out again after
    /// the grace period. False when the identity had no slot (nothing to do).
    /// </summary>
    public bool Retire(string identity, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(identity);

        lock (_gate)
        {
            if (!_byIdentity.Remove(identity, out var slot))
            {
                return false;
            }

            Volatile.Write(ref _live[slot], 0);
            _reusableAfterMs[slot] = nowMs + _graceMs;
            _retired.Add(slot);
            return true;
        }
    }

    /// <summary>Whether the slot stands for a policy right now. Lock-free: the sweeper's per-entry test.</summary>
    public bool IsLive(ushort slot) => slot != 0 && slot <= MaxSlots && Volatile.Read(ref _live[slot]) != 0;

    private ushort RentRetired(long nowMs)
    {
        for (var i = 0; i < _retired.Count; i++)
        {
            var slot = _retired[i];
            if (_reusableAfterMs[slot] > nowMs)
            {
                continue;
            }

            _retired.RemoveAt(i);

            // Before the slot can stand for a new policy, the old policy's buckets must be gone: otherwise the new
            // policy's first requests would spend tokens its predecessor had used.
            _table.ClearSlot(slot);
            return slot;
        }

        throw new InvalidOperationException(
            $"All {MaxSlots} rate-limit policy slots are in use and no retired slot has finished its grace period yet.");
    }
}
