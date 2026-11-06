using System.Security.Cryptography;

namespace Paduraksa.Core.Ids;

/// <summary>
/// Monotonic RFC 9562 UUIDv7 generator: 48-bit Unix ms timestamp, 12-bit counter in <c>rand_a</c>
/// (RFC 9562 §6.2 method 1), 62 random bits in <c>rand_b</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>Guid.CreateVersion7()</c> (banned repo-wide) fills <c>rand_a</c> with random bits, so two IDs created in the same
/// millisecond sort randomly. This generator keeps IDs from one process strictly increasing (in big-endian byte
/// order, and therefore in base62 string order), which keeps B-tree inserts append-only and makes
/// "created after" cursors exact.
/// </para>
/// <para>
/// The counter starts at a random value in [0, 2048) for each new millisecond, leaving at least 2048 IDs per
/// millisecond before the logical timestamp borrows from the next millisecond. If the clock goes backwards the
/// last timestamp is kept and the counter keeps incrementing. Lock-free: one CAS on a single <see cref="long"/>.
/// </para>
/// </remarks>
public sealed class UuidV7Generator(TimeProvider timeProvider)
{
    private const int CounterBits = 12;
    private const long MaxTimestamp = (1L << 48) - 1;
    private const int SeedMask = (1 << (CounterBits - 1)) - 1;

    /// <summary>Process-wide instance on <see cref="TimeProvider.System"/>, used by generated <c>New()</c>.</summary>
    // TODO: decide whether Shared should use the clock abstraction once it lands in Core.
    public static UuidV7Generator Shared { get; } = new(TimeProvider.System);

    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    // (unix ms << 12) | counter of the last issued ID.
    private long _state;

    public Guid Next()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes[6..]);
        long seed = ((bytes[6] << 8) | bytes[7]) & SeedMask;

        long now = Math.Clamp(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), 0, MaxTimestamp);
        long state;
        while (true)
        {
            long last = Volatile.Read(ref _state);
            state = now > (last >> CounterBits) ? (now << CounterBits) | seed : last + 1;
            if (Interlocked.CompareExchange(ref _state, state, last) == last)
            {
                break;
            }
        }

        long ms = state >> CounterBits;
        int counter = (int)(state & ((1 << CounterBits) - 1));
        bytes[0] = (byte)(ms >> 40);
        bytes[1] = (byte)(ms >> 32);
        bytes[2] = (byte)(ms >> 24);
        bytes[3] = (byte)(ms >> 16);
        bytes[4] = (byte)(ms >> 8);
        bytes[5] = (byte)ms;
        bytes[6] = (byte)(0x70 | (counter >> 8));
        bytes[7] = (byte)counter;
        bytes[8] = (byte)(0x80 | (bytes[8] & 0x3F));
        return new Guid(bytes, bigEndian: true);
    }
}
