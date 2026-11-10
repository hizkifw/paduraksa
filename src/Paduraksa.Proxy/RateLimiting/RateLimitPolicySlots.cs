namespace Paduraksa.Proxy.RateLimiting;

/// <summary>
/// Maps a rate-limit policy's stable identity to its 16-bit table slot: append-only, so a bucket survives every publish
/// of a config that still has the policy (PLAN §6.3). Slot 0 is reserved for "no policy"; the other 65 535 are handed
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
