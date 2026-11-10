using Paduraksa.Proxy.RateLimiting;

namespace Paduraksa.Tests.Proxy.RateLimiting;

public sealed class RateLimitSweeperTests
{
    private const ulong Hash = 0x0000_ABCD_EF01;
    private static readonly RateLimitParameters OnePerSecondBurstTen = new(1, 10);

    [Fact]
    public void ClearsAnEntryPastItsFullRefillWindow()
    {
        var (table, _, sweeper, slot) = Create();
        Consume(table, slot, OnePerSecondBurstTen, nowMs: 0);
        Assert.True(Contains(table, slot, Hash));

        // The window is burst / rate = 10 s; up to and including it, the bucket is refilled by the next touch anyway.
        Assert.Equal(0, sweeper.Sweep(nowMs: 9_999));
        Assert.True(Contains(table, slot, Hash));
        Assert.Equal(0, sweeper.Sweep(nowMs: 10_000));
        Assert.True(Contains(table, slot, Hash));

        Assert.Equal(1, sweeper.Sweep(nowMs: 10_001));
        Assert.False(Contains(table, slot, Hash));
    }

    [Fact]
    public void AnEntryTouchedBetweenTheReadAndTheClearSurvives()
    {
        var (table, _, sweeper, slot) = Create();
        Consume(table, slot, OnePerSecondBurstTen, nowMs: 0);

        // What a sweep reads before it decides: the entry's key and state.
        var index = IndexOf(table, slot, Hash);
        var key = table.KeyAt(index);
        var state = table.StateAt(index);

        // A request lands in between and spends one of the ten tokens its refill earned.
        Assert.True(table.TryAcquire(slot, Hash, OnePerSecondBurstTen, nowMs: 11_000, out _));

        // The clear the sweep would issue now finds a moved state, so the entry stays with nine tokens left.
        Assert.False(table.ClearIfUnchanged(index, key, state));
        Assert.True(Contains(table, slot, Hash));
        Assert.Equal(9, CountTakes(table, slot, OnePerSecondBurstTen, nowMs: 11_000));
        Assert.Equal(0, sweeper.Sweep(nowMs: 11_000));
    }

    [Fact]
    public void AnEntryTouchedAfterThePassReadTheClockIsNotStale()
    {
        var (table, _, sweeper, slot) = Create();
        Consume(table, slot, OnePerSecondBurstTen, nowMs: 5_000);

        // The pass sampled 4_000 before the request at 5_000 landed: the entry's time is ahead of the pass, so it stays.
        Assert.Equal(0, sweeper.Sweep(nowMs: 4_000));
        Assert.True(Contains(table, slot, Hash));
    }

    [Fact]
    public void ClearsTheEntriesOfARetiredSlotAtOnce()
    {
        var (table, slots, sweeper, slot) = Create();
        Assert.True(table.TryAcquire(slot, Hash, OnePerSecondBurstTen, nowMs: 1_000, out _));
        Assert.Equal(0, sweeper.Sweep(nowMs: 1_000));

        // The policy is gone from the config, so its buckets go on the next pass whatever their age.
        Assert.True(slots.Retire("p", nowMs: 1_000));
        Assert.Equal(1, sweeper.Sweep(nowMs: 1_000));
        Assert.False(Contains(table, slot, Hash));
    }

    [Fact]
    public void ASlotWithNoPublishedParametersIsLeftAlone()
    {
        // A live slot whose parameters the compiler hasn't published is not something the sweeper can judge.
        var (table, _, sweeper, slot) = Create(publish: false);
        Assert.True(table.TryAcquire(slot, Hash, OnePerSecondBurstTen, nowMs: 0, out _));

        Assert.Equal(0, sweeper.Sweep(nowMs: 1_000_000));
        Assert.True(Contains(table, slot, Hash));
    }

    [Fact]
    public void AnEmptyTableClearsNothing()
    {
        var (_, _, sweeper, _) = Create();
        Assert.Equal(0, sweeper.Sweep(nowMs: 1_000_000));
    }

    private static (RateLimitTable Table, RateLimitPolicySlots Slots, RateLimitSweeper Sweeper, ushort Slot) Create(bool publish = true)
    {
        RateLimitTable table = new(64);
        RateLimitPolicySlots slots = new(table);
        var slot = slots.GetOrAdd("p", nowMs: 0);
        if (publish)
        {
            RateLimitParameters[] bySlot = new RateLimitParameters[slot + 1];
            bySlot[slot] = OnePerSecondBurstTen;
            slots.Publish(new RateLimitPolicyParameters(bySlot));
        }

        return (table, slots, new RateLimitSweeper(table, slots), slot);
    }

    private static void Consume(RateLimitTable table, ushort slot, in RateLimitParameters parameters, long nowMs)
    {
        while (table.TryAcquire(slot, Hash, parameters, nowMs, out _))
        {
        }
    }

    private static int CountTakes(RateLimitTable table, ushort slot, in RateLimitParameters parameters, long nowMs)
    {
        var taken = 0;
        while (table.TryAcquire(slot, Hash, parameters, nowMs, out _))
        {
            taken++;
        }

        return taken;
    }

    private static bool Contains(RateLimitTable table, ushort slot, ulong hash) => IndexOf(table, slot, hash) >= 0;

    private static int IndexOf(RateLimitTable table, ushort slot, ulong hash)
    {
        var key = ((long)slot << 48) | (long)(hash & 0xFFFF_FFFF_FFFF);
        for (var i = 0; i < table.Capacity; i++)
        {
            if (table.KeyAt(i) == key)
            {
                return i;
            }
        }

        return -1;
    }
}
