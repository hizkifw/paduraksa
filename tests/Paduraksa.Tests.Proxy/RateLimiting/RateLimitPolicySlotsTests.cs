using Paduraksa.Proxy.RateLimiting;

namespace Paduraksa.Tests.Proxy.RateLimiting;

public sealed class RateLimitPolicySlotsTests
{
    [Fact]
    public void AnIdentityKeepsItsSlot()
    {
        RateLimitPolicySlots slots = new(new RateLimitTable(64));

        var api = slots.GetOrAdd("route:rte_01/api", nowMs: 0);
        var other = slots.GetOrAdd("route:rte_02/api", nowMs: 0);

        Assert.Equal((ushort)1, api);
        Assert.Equal((ushort)2, other);
        Assert.Equal(api, slots.GetOrAdd("route:rte_01/api", nowMs: 60_000));
        Assert.Equal(other, slots.GetOrAdd("route:rte_02/api", nowMs: 60_000));
        Assert.True(slots.IsLive(api));
        Assert.False(slots.IsLive(0));
    }

    [Fact]
    public void RetiringAnUnknownIdentityIsANoOp()
    {
        RateLimitPolicySlots slots = new(new RateLimitTable(64));

        Assert.False(slots.Retire("never-added", nowMs: 0));
    }

    [Fact]
    public void ARetiredSlotIsNotReusedBeforeItsGracePeriodIsOver()
    {
        RateLimitTable table = new(1_024);
        RateLimitPolicySlots slots = new(table, TimeSpan.FromMilliseconds(100));

        Assert.Equal((ushort)1, slots.GetOrAdd("p0", nowMs: 0));
        FillEverySlot(slots);

        // The registry's ceiling: 65 535 policies, and the slot after them is refused with a message that says why.
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = slots.GetOrAdd("one-too-many", nowMs: 0);
        });
        Assert.Contains(RateLimitPolicySlots.MaxSlots.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);

        Assert.True(slots.Retire("p0", nowMs: 0));
        Assert.False(slots.IsLive(1));

        // Inside the grace period the slot is neither live nor free, so there is still no slot to hand out.
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = slots.GetOrAdd("new", nowMs: 99);
        });

        Assert.Equal((ushort)1, slots.GetOrAdd("new", nowMs: 100));
        Assert.True(slots.IsLive(1));
    }

    [Fact]
    public void AReusedSlotDoesNotCarryTheOldPolicysBuckets()
    {
        RateLimitTable table = new(1_024);
        RateLimitPolicySlots slots = new(table, TimeSpan.FromMilliseconds(100));
        RateLimitParameters one = new(1, 1);

        var slot = slots.GetOrAdd("p0", nowMs: 0);
        Assert.True(table.TryAcquire(slot, 0x10, one, nowMs: 0, out _));
        Assert.False(table.TryAcquire(slot, 0x10, one, nowMs: 0, out _));

        FillEverySlot(slots);
        Assert.True(slots.Retire("p0", nowMs: 0));

        // The next policy on that slot starts with full buckets, not the retired one's empty bucket.
        var reused = slots.GetOrAdd("p1-reused", nowMs: 100);
        Assert.Equal(slot, reused);
        Assert.True(table.TryAcquire(reused, 0x10, one, nowMs: 100, out _));
        Assert.False(table.TryAcquire(reused, 0x10, one, nowMs: 100, out _));
    }

    [Fact]
    public void PublishedParametersAreOneWholeSet()
    {
        RateLimitTable table = new(64);
        RateLimitPolicySlots slots = new(table);
        Assert.Same(RateLimitPolicyParameters.Empty, slots.Parameters);

        var slot = slots.GetOrAdd("api", nowMs: 0);
        RateLimitParameters[] bySlot = new RateLimitParameters[slot + 1];
        bySlot[slot] = new(5, 10);
        RateLimitPolicyParameters published = new(bySlot);
        slots.Publish(published);

        Assert.Same(published, slots.Parameters);
        Assert.True(slots.Parameters.TryGet(slot, out var parameters));
        Assert.Equal(new RateLimitParameters(5, 10), parameters);
        Assert.False(slots.Parameters.TryGet((ushort)(slot + 1), out _));
        Assert.Equal(slot + 1, slots.Parameters.Count);
    }

    [Fact]
    public void AnEmptyOrDefaultEntryIsNoPolicy()
    {
        Assert.Equal(0, RateLimitPolicyParameters.Empty.Count);
        Assert.False(RateLimitPolicyParameters.Empty.TryGet(1, out _));
        Assert.False(new RateLimitPolicyParameters([default, new(1, 1)]).TryGet(0, out var parameters));
        Assert.Equal(0, parameters.Burst);
    }

    private static void FillEverySlot(RateLimitPolicySlots slots)
    {
        for (var i = 1; i < RateLimitPolicySlots.MaxSlots; i++)
        {
            slots.GetOrAdd($"p{i}", nowMs: 0);
        }
    }
}
