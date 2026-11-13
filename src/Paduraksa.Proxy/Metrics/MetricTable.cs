using System.Runtime.CompilerServices;

namespace Paduraksa.Proxy.Metrics;

/// <summary>
/// Counters for one subject, <c>long[]</c> indexed by <c>slot * stride + metric</c> inside a per-core stripe (docs/design/observability.md "Metrics (M9)"). A
/// request adds to the stripe of the core it runs on, so cores don't fight over a cache line; a reader sums the stripes.
/// </summary>
/// <remarks>
/// <para><b>Layout.</b> Slots are allocated in chunks of <see cref="ChunkSlots"/>, one array per chunk holding every stripe's
/// block: <c>(stripe * ChunkSlots + slot % ChunkSlots) * stride</c>. The stride is a multiple of 8 longs (a cache line), so no
/// two slots share a line and a stripe's block is kilobytes away from the next stripe's. Memory grows with the slots in use,
/// a chunk at a time; the chunk table itself is fixed, so the hot path never sees it move.</para>
/// <para><b>Threads.</b> <see cref="Increment"/> and <see cref="Add"/> are one <see cref="Interlocked"/> each. A slot's chunk is
/// created, under a lock, before the slot is given to any compiled snapshot, and the snapshot's publication orders that
/// before every request that uses it. A read concurrent with writes sees a consistent-enough total (each counter is exact
/// at some moment); nothing here needs more.</para>
/// </remarks>
public sealed class MetricTable
{
    /// <summary>Slots per chunk.</summary>
    public const int ChunkSlots = 16;

    private readonly long[]?[] _chunks = new long[MetricSlotRegistry.MaxSlots / ChunkSlots][];
    private readonly Lock _grow = new();

    /// <param name="stride">Longs per slot and stripe; a positive multiple of 8.</param>
    /// <param name="stripes">Stripes: a power of two, normally the core count rounded up (see <see cref="StripesFor"/>).</param>
    public MetricTable(int stride, int stripes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stride);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stripes);
        if (stride % 8 != 0)
        {
            throw new ArgumentException("A slot's stride must be a whole number of cache lines (a multiple of 8 longs).", nameof(stride));
        }

        if ((stripes & (stripes - 1)) != 0)
        {
            throw new ArgumentException("The stripe count must be a power of two.", nameof(stripes));
        }

        Stride = stride;
        Stripes = stripes;
        StripeMask = stripes - 1;
    }

    /// <summary>The upper bound on stripes: more would only cost memory, and sums.</summary>
    public const int MaxStripes = 16;

    public int Stride { get; }

    public int Stripes { get; }

    internal int StripeMask { get; }

    /// <summary>The stripe count for a machine with <paramref name="processors"/> cores: a power of two, at most <see cref="MaxStripes"/>.</summary>
    public static int StripesFor(int processors) =>
        (int)Math.Min(MaxStripes, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(processors, 1)));

    /// <summary>The stripe of the core the caller runs on.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CurrentStripe() => Thread.GetCurrentProcessorId() & StripeMask;

    /// <summary>Makes room for <paramref name="slot"/>'s chunk. Cold path, takes a lock.</summary>
    public void Ensure(int slot)
    {
        var index = slot / ChunkSlots;
        if (_chunks[index] is not null)
        {
            return;
        }

        lock (_grow)
        {
            _chunks[index] ??= new long[Stripes * ChunkSlots * Stride];
        }
    }

    /// <summary>Zeroes the slot in every stripe: a reused slot starts its new owner at zero.</summary>
    public void Clear(int slot)
    {
        if (_chunks[slot / ChunkSlots] is not { } chunk)
        {
            return;
        }

        for (var stripe = 0; stripe < Stripes; stripe++)
        {
            var start = Offset(stripe, slot, 0);
            for (var i = 0; i < Stride; i++)
            {
                Volatile.Write(ref chunk[start + i], 0);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Increment(int stripe, int slot, int metric)
    {
        if (_chunks[slot / ChunkSlots] is { } chunk)
        {
            Interlocked.Increment(ref chunk[Offset(stripe, slot, metric)]);
        }
    }

    /// <summary><paramref name="delta"/> more for <paramref name="metric"/>. 0 B.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(int stripe, int slot, int metric, long delta)
    {
        if (_chunks[slot / ChunkSlots] is { } chunk)
        {
            Interlocked.Add(ref chunk[Offset(stripe, slot, metric)], delta);
        }
    }

    /// <summary>The sum of one metric over every stripe.</summary>
    public long Sum(int slot, int metric)
    {
        if (_chunks[slot / ChunkSlots] is not { } chunk)
        {
            return 0;
        }

        long total = 0;
        for (var stripe = 0; stripe < Stripes; stripe++)
        {
            total += Volatile.Read(ref chunk[Offset(stripe, slot, metric)]);
        }

        return total;
    }

    /// <summary>Adds every stripe's values for the slot into <paramref name="totals"/> (cleared first): a whole slot in one pass.</summary>
    public void SumInto(int slot, Span<long> totals)
    {
        totals.Clear();
        if (_chunks[slot / ChunkSlots] is not { } chunk)
        {
            return;
        }

        var count = Math.Min(totals.Length, Stride);
        for (var stripe = 0; stripe < Stripes; stripe++)
        {
            var start = Offset(stripe, slot, 0);
            for (var i = 0; i < count; i++)
            {
                totals[i] += Volatile.Read(ref chunk[start + i]);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Offset(int stripe, int slot, int metric) => (((stripe * ChunkSlots) + (slot % ChunkSlots)) * Stride) + metric;
}
