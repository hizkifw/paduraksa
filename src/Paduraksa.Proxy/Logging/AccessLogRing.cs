namespace Paduraksa.Proxy.Logging;

/// <summary>
/// The access-log buffer between the request path and the writer: a fixed-size,
/// lock-free, <b>multi-producer single-consumer</b> ring of <see cref="AccessLogEntry"/> structs. The hot path only
/// reserves a slot and writes a struct into it; a background service is the one reader
/// (<c>AccessLogDrainService</c>).
/// </summary>
/// <remarks>
/// <para><b>Never blocks a request.</b> When the ring is full the entry is dropped and <see cref="Dropped"/> counts it; the
/// drain reports the count at Warning so a slow or full disk is visible in the process log instead of stalling the proxy.</para>
/// <para><b>Algorithm.</b> Dmitry Vyukov's bounded queue: one sequence number per slot. A producer claims a position with a
/// CAS on the tail, writes the entry, then publishes it with a release store of the slot's sequence; the consumer only
/// reads a slot whose sequence says it is published, and frees it by advancing that sequence a whole lap. So a producer
/// can never overwrite a slot the consumer is reading, no lock or sleep is involved, and each producer's entries keep
/// their own order (entries of different producers may interleave).</para>
/// <para><b>A stalled producer.</b> A producer that loses its turn for a whole lap (it is descheduled while the other
/// producers and the consumer go round the ring) drops its line and counts it, even though the ring may have room again by
/// the time it runs: the queue cannot tell "the consumer has read my slot" from "the consumer is about to", so it never
/// waits and never rewrites.</para>
/// <para><b>Memory:</b> one slot array of <see cref="Capacity"/> slots allocated once. A slot is padded to
/// <see cref="SlotBytes"/> (192 bytes: the sequence number and the entry) so that slots on either side don't share
/// a cache line: producers write different slots, and a line shared by two of them would ping-pong between cores. 64-byte
/// lines, not 128, because every x86-64 and ARM64 core this runs on has 64-byte lines, and a slot bigger than a line can't
/// share one as long as it is aligned to a line boundary. The default 16 384 slots are about 3 MiB.</para>
/// </remarks>
public sealed class AccessLogRing
{
    /// <summary>Slots of a ring built without a size setting.</summary>
    public const int DefaultCapacity = 16_384;

    /// <summary>Smallest capacity a configured ring may be (the class itself accepts any power of two, tests included).</summary>
    public const int MinimumCapacity = 1_024;

    /// <summary>Largest accepted capacity: about 192 MiB of slots.</summary>
    public const int MaximumCapacity = 1 << 20;

    /// <summary>Bytes one slot occupies: three cache lines, so neighbouring slots never share one (a test pins it).</summary>
    internal const int SlotBytes = 192;

    private readonly Cell[] _cells;
    private readonly int _mask;
    private long _tail;
    private long _head;
    private long _dropped;

    /// <summary>A ring of <paramref name="capacity"/> slots, a power of two.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> isn't a power of two between 2 and
    /// <see cref="MaximumCapacity"/> (the <see cref="MinimumCapacity"/> floor is the option validator's, not the class's:
    /// a test can use a tiny ring).</exception>
    public AccessLogRing(int capacity = DefaultCapacity)
    {
        if (capacity < 2 || capacity > MaximumCapacity || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity), capacity, $"The access-log ring size must be a power of two between 2 and {MaximumCapacity}.");
        }

        Capacity = capacity;
        _mask = capacity - 1;
        _cells = new Cell[capacity];
        for (var i = 0; i < capacity; i++)
        {
            _cells[i].Sequence = i;
        }
    }

    public int Capacity { get; }

    /// <summary>The bytes one slot really takes; a test pins it to <see cref="SlotBytes"/>, so growing the entry shows up.</summary>
    internal static int SlotSizeBytes => System.Runtime.CompilerServices.Unsafe.SizeOf<Cell>();

    /// <summary>Entries dropped so far (the ring was full), across every producer.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Entries written so far, across every producer; the distance to <see cref="EntriesRead"/> is what is queued.</summary>
    public long EntriesWritten => Interlocked.Read(ref _tail);

    /// <summary>Entries the consumer has read so far.</summary>
    public long EntriesRead => Interlocked.Read(ref _head);

    /// <summary>Entries written and not yet read: how far the drain is behind.</summary>
    public long Pending => EntriesWritten - EntriesRead;

    /// <summary>
    /// Writes <paramref name="entry"/> to the next slot. False (and the drop counted) when the ring is full: the caller
    /// lets the line go rather than wait. Allocation-free; safe from any number of threads.
    /// </summary>
    public bool TryWrite(in AccessLogEntry entry)
    {
        var position = Interlocked.Read(ref _tail);
        while (true)
        {
            ref var cell = ref _cells[(int)(position & _mask)];
            var sequence = Volatile.Read(ref cell.Sequence) - position;
            if (sequence == 0)
            {
                if (Interlocked.CompareExchange(ref _tail, position + 1, position) != position)
                {
                    position = Interlocked.Read(ref _tail);
                    continue;
                }

                cell.Entry = entry;
                Volatile.Write(ref cell.Sequence, position + 1);
                return true;
            }

            if (sequence < 0)
            {
                // A whole lap ahead: the consumer hasn't caught up, so this slot still holds unread data.
                Interlocked.Increment(ref _dropped);
                return false;
            }

            position = Interlocked.Read(ref _tail);
        }
    }

    /// <summary>Reads the oldest entry; only the one consumer may call it.</summary>
    public bool TryRead(out AccessLogEntry entry)
    {
        var position = _head;
        ref var cell = ref _cells[(int)(position & _mask)];
        if (Volatile.Read(ref cell.Sequence) - (position + 1) < 0)
        {
            entry = default;
            return false;
        }

        entry = cell.Entry;
        Volatile.Write(ref cell.Sequence, position + Capacity);
        Volatile.Write(ref _head, position + 1);
        return true;
    }

    /// <summary>The drops since the last call, and 0 afterwards: what the drain reports without repeating itself.</summary>
    public long TakeDropped() => Interlocked.Exchange(ref _dropped, 0);

    /// <summary>
    /// One slot. <see cref="Sequence"/> is the position it holds, +1 once published, +Capacity once read again. Padded to
    /// <see cref="SlotBytes"/> so that no two slots share a cache line (see the remarks).
    /// </summary>
    private struct Cell
    {
        public long Sequence;
        // 8 + 184 (the entry's fields) = 192, three 64-byte lines, so a slot begins on a line boundary and owns every line it
        // covers. No padding is left: RingTests pins the total, and a bigger entry means growing the slot to four lines.
        public AccessLogEntry Entry;
    }
}
