using System.Security.Cryptography;

namespace Paduraksa.Core.Ids;

/// <summary>
/// Generates <see cref="IdKind.Int64Ordered"/> values: <c>0 | 44-bit ms since <see cref="Epoch"/> | 19-bit counter</c>.
/// </summary>
/// <remarks>
/// <para>
/// The sign bit is always 0, so values are positive and sort the same as signed or unsigned integers. 44 bits of
/// milliseconds last ~557 years past 2025-01-01 UTC.
/// </para>
/// <para>
/// The 19-bit counter is seeded randomly when the generator is created and increments on every ID (wrapping
/// within its 19 bits when a new millisecond starts). Within one millisecond, an overflowing counter carries into
/// the timestamp, i.e. the logical clock advances ahead of real time. If the clock goes backwards, the last
/// logical timestamp is kept. Every ID is therefore strictly greater than the previous one from the same instance.
/// Lock-free: one CAS on a single <see cref="long"/> holding the last issued value.
/// </para>
/// <para>
/// <b>Predictable:</b> consecutive IDs differ by small, guessable deltas, so they can be enumerated. Use this kind
/// only for internal/admin-only IDs (audit events, metric rollups, log indexes), never for anything exposed to
/// untrusted clients; use <see cref="IdKind.UuidV4"/> for that.
/// </para>
/// <para>
/// Single-node only: two generator instances (or processes) can produce the same value. After a restart the clock
/// may be behind the last persisted ID, so the owner of the table must call <see cref="Observe"/> with the
/// largest stored ID (e.g. <c>MAX(id)</c>) before generating new ones.
/// </para>
/// </remarks>
public sealed class Int64OrderedIdGenerator
{
    public const int TimestampBits = 44;
    public const int CounterBits = 19;

    /// <summary>Custom epoch: 2025-01-01T00:00:00Z.</summary>
    public static readonly DateTimeOffset Epoch = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const long CounterMask = (1L << CounterBits) - 1;
    private const long MaxTimestamp = (1L << TimestampBits) - 1;
    private static readonly long EpochUnixMilliseconds = Epoch.ToUnixTimeMilliseconds();

    /// <summary>Process-wide instance on <see cref="TimeProvider.System"/>, used by generated <c>New()</c>.</summary>
    // Shared stays on the system clock; a caller that needs another clock makes its own.
    public static Int64OrderedIdGenerator Shared { get; } = new(TimeProvider.System);

    private readonly TimeProvider _timeProvider;

    // The last issued value. Starts at timestamp 0 with a random counter.
    private long _last;

    public Int64OrderedIdGenerator(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _last = RandomNumberGenerator.GetInt32(1 << CounterBits);
    }

    /// <summary>Extracts the (logical) creation time of an ID.</summary>
    public static DateTimeOffset GetTimestamp(long id) =>
        DateTimeOffset.FromUnixTimeMilliseconds(EpochUnixMilliseconds + (id >> CounterBits));

    /// <summary>
    /// Ensures every later <see cref="Next"/> returns a value greater than <paramref name="lastIssued"/>. Call at
    /// startup with the largest persisted ID so IDs stay unique and ordered when the clock is behind it.
    /// </summary>
    public void Observe(long lastIssued)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lastIssued);
        long current = Volatile.Read(ref _last);
        while (lastIssued > current)
        {
            long seen = Interlocked.CompareExchange(ref _last, lastIssued, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }

    public long Next()
    {
        long now = Math.Clamp(
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() - EpochUnixMilliseconds, 0, MaxTimestamp);
        while (true)
        {
            long last = Volatile.Read(ref _last);
            long next;
            if (now > (last >> CounterBits))
            {
                next = (now << CounterBits) | ((last + 1) & CounterMask);
            }
            else if (last == long.MaxValue)
            {
                throw new InvalidOperationException("Int64Ordered ID space exhausted.");
            }
            else
            {
                next = last + 1; // same or earlier ms: bump the counter, carrying into the timestamp on overflow
            }

            if (Interlocked.CompareExchange(ref _last, next, last) == last)
            {
                return next;
            }
        }
    }
}
