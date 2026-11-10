using Paduraksa.Proxy.RateLimiting;

namespace Paduraksa.Tests.Proxy.RateLimiting;

public sealed class RateLimitTableProbeTests
{
    private const ushort Slot = 1;

    // A 16-entry table masks the low 4 bits, so these hashes all start at index 0 and therefore share a probe window.
    private static ulong Hash(int index) => 0x1000UL + ((ulong)index * 16);

    private static readonly RateLimitParameters One = new(1, 1);
    private static readonly RateLimitParameters Two = new(1, 2);

    [Fact]
    public void AKeyIsFoundAtItsProbeOffset()
    {
        RateLimitTable table = new(16);

        Assert.True(table.TryAcquire(Slot, Hash(0), One, nowMs: 0, out _));
        Assert.True(table.TryAcquire(Slot, Hash(1), One, nowMs: 0, out _));

        // The second key lives at offset 1; each key still has its own bucket.
        Assert.False(table.TryAcquire(Slot, Hash(0), One, nowMs: 0, out _));
        Assert.False(table.TryAcquire(Slot, Hash(1), One, nowMs: 0, out _));
        Assert.True(table.TryAcquire(Slot, Hash(0), One, nowMs: 1_000, out _));
    }

    [Fact]
    public void PoliciesWithTheSamePartitionHashHaveSeparateBuckets()
    {
        RateLimitTable table = new(16);

        Assert.True(table.TryAcquire(1, Hash(0), One, nowMs: 0, out _));
        Assert.True(table.TryAcquire(2, Hash(0), One, nowMs: 0, out _));

        // The slot is part of the key, so the second policy's bucket was not the first one's.
        Assert.False(table.TryAcquire(1, Hash(0), One, nowMs: 0, out _));
        Assert.False(table.TryAcquire(2, Hash(0), One, nowMs: 0, out _));
    }

    [Fact]
    public void TheNinthKeyEvictsTheOldestOfTheProbeWindow()
    {
        RateLimitTable table = new(16);

        // Eight keys fill the window, each exhausted and touched one millisecond after the one before: key 0 is oldest.
        for (var i = 0; i < RateLimitTable.ProbeLength; i++)
        {
            Assert.True(table.TryAcquire(Slot, Hash(i), One, nowMs: i, out _));
            Assert.False(table.TryAcquire(Slot, Hash(i), One, nowMs: i, out _));
        }

        // The ninth key has nowhere to go but key 0's slot, and it starts with a full bucket.
        Assert.True(table.TryAcquire(Slot, Hash(8), One, nowMs: 100, out _));
        Assert.False(table.TryAcquire(Slot, Hash(8), One, nowMs: 100, out _));

        // The evicted key starts full again...
        Assert.True(table.TryAcquire(Slot, Hash(0), Two, nowMs: 100, out _));
        Assert.True(table.TryAcquire(Slot, Hash(0), Two, nowMs: 100, out _));
        Assert.False(table.TryAcquire(Slot, Hash(0), Two, nowMs: 100, out _));

        // ...while the keys it was probed with still have their own (empty) buckets.
        for (var i = 2; i < RateLimitTable.ProbeLength; i++)
        {
            Assert.False(table.TryAcquire(Slot, Hash(i), One, nowMs: 100, out _), $"key {i} lost its bucket");
        }
    }
}
