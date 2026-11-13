namespace Paduraksa.Proxy.RateLimiting;

/// <summary>
/// The process's one rate-limit bucket table: a fixed-size, open-addressed, lock-free table of token buckets keyed by
/// (policy slot, partition hash) - docs/design/waf.md "Rate limiting". A call allocates nothing and never blocks.
/// </summary>
/// <remarks>
/// <para><b>Entries.</b> Two <see cref="long"/> arrays of <see cref="Capacity"/> entries, the same index in each. An
/// entry's key (bits 63-48 = policy slot, bits 47-0 = partition hash) is claimed with a
/// <see cref="Interlocked.CompareExchange(ref long, long, long)"/> from 0, so 0 means empty and slot 0 can never be
/// stored (it is "no policy"). Its state is (<b>bits 63-24 = last refill in ms since process start</b>, a 40-bit field
/// that a process cannot outlive; <b>bits 23-0 = tokens in 1/16 units</b>, which is 20.4 fixed point, so a burst of at
/// most 1 048 575 whole tokens). A new entry starts full.</para>
/// <para><b>Probing.</b> The start index is the low bits of the partition hash; a key is looked for in at most
/// <see cref="ProbeLength"/> consecutive entries and then either found there or given the oldest of them (least recently
/// refilled). The hash is per-process random (<see cref="RateLimitPartition"/>), so a client cannot precompute
/// collisions; a collision only shares a bucket with another key, which makes limits stricter, never looser, and the
/// table's size bounds memory whatever the traffic.</para>
/// <para><b>Accepted races.</b> Two words per entry cannot be claimed atomically, so these interleavings are left open on
/// purpose, and none of them is unbounded because every touch recomputes the tokens from the state it read and clamps
/// them to its policy's burst:</para>
/// <list type="bullet">
/// <item>Threads probing for the same key in a window with no empty slot can each evict a different entry for it
/// (their reads of which entry is oldest differ), so with k such threads that key briefly has k buckets and may pass
/// (k - 1) bursts extra, once; probing stops at the first match, so only one is ever used again, and the sweeper clears
/// the others once idle.</item>
/// <item>A thread that resolved a key can have its token update land in a slot another thread has just evicted and
/// re-claimed. The successor's next touch reads tokens that aren't its own and clamps them to its burst, so the error is
/// at most one burst, once, and only for a slot being evicted at that instant.</item>
/// <item>A thread that arrives between another thread's key claim and its state write sees the previous occupant's (or a
/// cleared) state: the refill then saturates at its own burst, so it can get a full bucket once. Eviction also throws away
/// the evicted bucket's refill; the new key starts full, which is what it would have been.</item>
/// <item>The sweeper clears an entry by CASing its state to 0 and then its key to 0; a request that meets the cleared
/// state refills from zero to a full bucket, exactly what the sweeper was aiming at.</item>
/// </list>
/// </remarks>
public sealed class RateLimitTable
{
    /// <summary>Entries in a table built without a configured size: 256 Ki, about 4 MiB of keys and states.</summary>
    public const int DefaultCapacity = 262_144;

    /// <summary>Smallest configurable table (the class accepts anything from 2, tests included).</summary>
    public const int MinimumCapacity = 1_024;

    /// <summary>Largest configurable table: 16 Mi entries, about 256 MiB.</summary>
    public const int MaximumCapacity = 16 * 1_024 * 1_024;

    /// <summary>Entries a key's probe looks at before the oldest of them is evicted.</summary>
    public const int ProbeLength = 8;

    /// <summary>The widest last-refill value the 40-bit field holds: 2<sup>40</sup> - 1 ms, about 34.8 years.</summary>
    public const long MaxTimeMs = (1L << 40) - 1;

    private const int TokenShift = 24;
    private const long TokenMask = (1L << TokenShift) - 1;
    private const int KeySlotShift = 48;
    private const long PartitionMask = (1L << KeySlotShift) - 1;

    // How often a probe starts over when it lost the slot it was going to claim or evict. A probe only restarts on such
    // a loss, so this is reached only when a whole window keeps moving under the thread.
    private const int MaxProbeAttempts = 4;

    private readonly long[] _keys;
    private readonly long[] _states;
    private readonly int _mask;

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> isn't a power of two from 2 to
    /// <see cref="MaximumCapacity"/> (the <see cref="MinimumCapacity"/> floor is the option validator's, not the class's:
    /// tests use tiny tables).</exception>
    public RateLimitTable(int capacity = DefaultCapacity)
    {
        if (capacity < 2 || capacity > MaximumCapacity || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity), capacity, $"The rate-limit table size must be a power of two between 2 and {MaximumCapacity}.");
        }

        _keys = new long[capacity];
        _states = new long[capacity];
        _mask = capacity - 1;
        Capacity = capacity;
    }

    public int Capacity { get; }

    /// <summary>
    /// Takes one token from the bucket of (<paramref name="slot"/>, <paramref name="partitionHash"/>), creating it full
    /// when the key isn't in the table. False means the bucket is empty; <paramref name="retryAfterSeconds"/> is then the
    /// whole seconds until one token is available, at least 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is 0, which is "no policy".</exception>
    public bool TryAcquire(ushort slot, ulong partitionHash, in RateLimitParameters parameters, long nowMs, out int retryAfterSeconds)
    {
        Validate(slot, parameters);

        var hash = partitionHash & (ulong)PartitionMask;
        var key = ((long)slot << KeySlotShift) | (long)hash;
        var start = (int)(hash & (ulong)_mask);

        for (var attempt = 0; attempt < MaxProbeAttempts; attempt++)
        {
            var oldestIndex = -1;
            var oldestKey = 0L;
            var oldestTime = long.MaxValue;
            var lostEmpty = false;

            for (var i = 0; i < ProbeLength; i++)
            {
                var index = (start + i) & _mask;
                var observed = Volatile.Read(ref _keys[index]);
                if (observed == key)
                {
                    return TryTake(index, parameters, nowMs, out retryAfterSeconds);
                }

                if (observed == 0)
                {
                    if (Interlocked.CompareExchange(ref _keys[index], key, 0) == 0)
                    {
                        StartFull(index, parameters.BurstUnits, nowMs);
                        retryAfterSeconds = 0;
                        return true;
                    }

                    // Someone else claimed it first. Probe from the start again rather than on: the winner may well have
                    // been this key, and claiming a later slot for it would give it two buckets.
                    lostEmpty = true;
                    break;
                }

                var time = TimeOf(Volatile.Read(ref _states[index]));
                if (time < oldestTime)
                {
                    oldestIndex = index;
                    oldestKey = observed;
                    oldestTime = time;
                }
            }

            if (lostEmpty)
            {
                continue;
            }

            if (oldestIndex >= 0 && Interlocked.CompareExchange(ref _keys[oldestIndex], key, oldestKey) == oldestKey)
            {
                StartFull(oldestIndex, parameters.BurstUnits, nowMs);
                retryAfterSeconds = 0;
                return true;
            }

            // The window moved under this probe: the candidate was taken, evicted or refilled since it was read.
        }

        // Four probes in a row lost every slot they picked, so no slot could be claimed without waiting. This request goes
        // through without a token rather than stalling the gate: one such request per four lost probes, at whatever rate
        // window churn produces them (a lone client on its own key never gets here; a found key spins in TryTake).
        retryAfterSeconds = 0;
        return true;
    }

    /// <summary>
    /// What a request would get, without taking a token or writing anything: the request simulator's read. True when the
    /// request would be allowed, and <paramref name="tokensLeft"/> is then the whole tokens its bucket would hold
    /// afterwards; false when it would be refused, and <paramref name="retryAfterSeconds"/> is its wait (at least 1).
    /// A key that isn't in the table yet reads as a full bucket, which is what a take would create.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is 0, which is "no policy".</exception>
    public bool TryPeek(ushort slot, ulong partitionHash, in RateLimitParameters parameters, long nowMs, out int tokensLeft, out int retryAfterSeconds)
    {
        Validate(slot, parameters);

        var hash = partitionHash & (ulong)PartitionMask;
        var key = ((long)slot << KeySlotShift) | (long)hash;
        var start = (int)(hash & (ulong)_mask);

        for (var i = 0; i < ProbeLength; i++)
        {
            var index = (start + i) & _mask;
            var observed = Volatile.Read(ref _keys[index]);
            if (observed == key)
            {
                return Peek(index, parameters, nowMs, out tokensLeft, out retryAfterSeconds);
            }

            if (observed == 0)
            {
                // A take would claim this entry and start it full.
                break;
            }
        }

        // Either an empty entry or a full probe window: a take claims (or evicts for) this key and starts it full.
        tokensLeft = Math.Max(0, parameters.Burst - 1);
        retryAfterSeconds = 0;
        return true;
    }

    /// <summary>Every bucket of one policy slot, for the registry to reuse the slot without handing out old buckets.</summary>
    internal int ClearSlot(ushort slot)
    {
        var cleared = 0;
        for (var i = 0; i < _keys.Length; i++)
        {
            // Retried because a request can refill the bucket between this read and the CAS. Past the grace period none
            // should be left, so a few attempts settle it; a bucket that keeps moving is left to this pass's failure.
            for (var attempt = 0; attempt < MaxProbeAttempts; attempt++)
            {
                var key = Volatile.Read(ref _keys[i]);
                if ((ushort)(key >> KeySlotShift) != slot)
                {
                    break;
                }

                if (ClearIfUnchanged(i, key, Volatile.Read(ref _states[i])))
                {
                    cleared++;
                    break;
                }
            }
        }

        return cleared;
    }

    internal long KeyAt(int index) => Volatile.Read(ref _keys[index]);

    internal long StateAt(int index) => Volatile.Read(ref _states[index]);

    /// <summary>
    /// Clears the entry at <paramref name="index"/> only if it still holds the observed state and key. CASing the state
    /// first is what keeps the sweeper from clearing an entry a request touched after the sweep read it: the request's
    /// CAS moves the state, so this one fails and the entry stays.
    /// </summary>
    internal bool ClearIfUnchanged(int index, long observedKey, long observedState)
    {
        if (Interlocked.CompareExchange(ref _states[index], 0, observedState) != observedState)
        {
            return false;
        }

        // A request that met the cleared state may have started the entry again between the two CASes; then the key has
        // not changed, and this clears a bucket that was about to be full anyway.
        Interlocked.CompareExchange(ref _keys[index], 0, observedKey);
        return true;
    }

    internal static ushort SlotOf(long key) => (ushort)(key >> KeySlotShift);

    internal static long TimeOf(long state) => (state >> TokenShift) & MaxTimeMs;

    internal static long TokensOf(long state) => state & TokenMask;

    private static void Validate(ushort slot, in RateLimitParameters parameters)
    {
        if (slot == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "Slot 0 is 'no policy' and never owns a bucket.");
        }

        if (parameters.BurstUnits <= 0)
        {
            // default(RateLimitParameters) is "no policy": stored, it would hold garbage tokens and divide by its zero rate
            // later, so a caller that skipped the slot's parameters fails here instead.
            throw new ArgumentException("The parameters are default: look the slot's parameters up first.", nameof(parameters));
        }
    }

    private bool TryTake(int index, in RateLimitParameters parameters, long nowMs, out int retryAfterSeconds)
    {
        var burstUnits = parameters.BurstUnits;
        while (true)
        {
            var observed = Volatile.Read(ref _states[index]);
            // A sample older than the stored time (a thread that read the clock before another's take landed) earns no
            // refill and must not move the time back: masked, its small negative difference would read as 2^40 ms and
            // refill an empty bucket. The mask is only for the 40-bit wrap, where the difference is small and positive.
            var stored = TimeOf(observed);
            var diff = nowMs - stored;
            var elapsed = diff < 0 ? 0 : diff & MaxTimeMs;
            var time = diff < 0 ? stored : nowMs;
            var available = RateLimitParameters.Clamp(TokensOf(observed), parameters.RefillUnits(elapsed), burstUnits);

            if (available < RateLimitParameters.TokenUnit)
            {
                retryAfterSeconds = parameters.RetryAfterSeconds(RateLimitParameters.TokenUnit - available);
                return false;
            }

            if (Interlocked.CompareExchange(ref _states[index], Pack(time, available - RateLimitParameters.TokenUnit), observed) == observed)
            {
                retryAfterSeconds = 0;
                return true;
            }
        }
    }

    /// <summary>
    /// <see cref="TryTake"/>'s arithmetic without its CAS: the same refill, the same clamp to the burst, the same answer a
    /// request would get, and nothing written. It may read a state a concurrent take is replacing; a simulation needs an
    /// answer, not a guarantee.
    /// </summary>
    private bool Peek(int index, in RateLimitParameters parameters, long nowMs, out int tokensLeft, out int retryAfterSeconds)
    {
        var observed = Volatile.Read(ref _states[index]);
        var stored = TimeOf(observed);
        var diff = nowMs - stored;
        var elapsed = diff < 0 ? 0 : diff & MaxTimeMs;
        var available = RateLimitParameters.Clamp(TokensOf(observed), parameters.RefillUnits(elapsed), parameters.BurstUnits);

        if (available < RateLimitParameters.TokenUnit)
        {
            tokensLeft = 0;
            retryAfterSeconds = parameters.RetryAfterSeconds(RateLimitParameters.TokenUnit - available);
            return false;
        }

        // What the bucket would hold after the token this request would take.
        tokensLeft = (int)((available - RateLimitParameters.TokenUnit) / RateLimitParameters.TokenUnit);
        retryAfterSeconds = 0;
        return true;
    }

    private void StartFull(int index, long burstUnits, long nowMs) =>
        Interlocked.Exchange(ref _states[index], Pack(nowMs, burstUnits - RateLimitParameters.TokenUnit));

    private static long Pack(long nowMs, long units) => ((nowMs & MaxTimeMs) << TokenShift) | (units & TokenMask);
}
