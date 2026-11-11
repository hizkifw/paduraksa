using Paduraksa.Proxy.Metrics;

namespace Paduraksa.Tests.Proxy.Metrics;

public sealed class MetricSlotRegistryTests
{
    private static readonly Guid A = new("00000000-0000-7000-8000-00000000000a");
    private static readonly Guid B = new("00000000-0000-7000-8000-00000000000b");
    private static readonly Guid C = new("00000000-0000-7000-8000-00000000000c");

    private readonly List<(int Slot, bool Reused)> _prepared = [];

    private MetricSlotRegistry Create(int grace = 100, int reserved = 0, int maxSlots = MetricSlotRegistry.MaxSlots) =>
        new(MetricSubject.Route, TimeSpan.FromMilliseconds(grace), (slot, reused) => _prepared.Add((slot, reused)), reserved, maxSlots);

    [Fact]
    public void AnObjectKeepsItsSlotAndSlotsAreHandedOutOnce()
    {
        var slots = Create();

        var a = slots.GetOrAdd(A, nowMs: 0);
        var b = slots.GetOrAdd(B, nowMs: 0);

        Assert.Equal(0, a);
        Assert.Equal(1, b);
        Assert.Equal(a, slots.GetOrAdd(A, nowMs: 60_000));
        Assert.Equal(A, slots.OwnerOf(a));
        Assert.True(slots.IsLive(a));
        Assert.Equal(2, slots.HighWater);
        Assert.Equal([(0, false), (1, false)], _prepared);
    }

    [Fact]
    public void ReservedSlotsBelongToNoOneAndSurviveEveryPublish()
    {
        var slots = Create(reserved: 1);

        var a = slots.GetOrAdd(A, nowMs: 0);
        Assert.Equal(1, a);
        Assert.True(slots.IsLive(0));
        Assert.Equal(Guid.Empty, slots.OwnerOf(0));

        slots.Publish([], nowMs: 10_000);

        Assert.True(slots.IsLive(0));
        Assert.False(slots.IsLive(a));
    }

    [Fact]
    public void ARetiredSlotKeepsItsOwnerAndIsRentedOnlyAfterTheGracePeriod()
    {
        var slots = Create(grace: 100, maxSlots: 2);
        var a = slots.GetOrAdd(A, nowMs: 0);
        _ = slots.GetOrAdd(B, nowMs: 0);
        _prepared.Clear();

        Assert.True(slots.Retire(A, nowMs: 1_000));
        Assert.False(slots.IsLive(a));
        Assert.Equal(A, slots.OwnerOf(a));

        // Inside the grace period the slot is neither live nor free, and the table is full.
        Assert.Throws<InvalidOperationException>(() => slots.GetOrAdd(C, nowMs: 1_099));

        var rented = slots.GetOrAdd(C, nowMs: 1_100);
        Assert.Equal(a, rented);
        Assert.Equal(C, slots.OwnerOf(rented));
        // The new owner's counters are cleared before it is handed the slot.
        Assert.Equal([(a, true)], _prepared);
    }

    [Fact]
    public void ARetiredSlotIsReusedBeforeTheTableGrows()
    {
        var slots = Create(grace: 100);
        var a = slots.GetOrAdd(A, nowMs: 0);
        _ = slots.GetOrAdd(B, nowMs: 0);
        slots.Retire(A, nowMs: 0);

        Assert.Equal(a, slots.GetOrAdd(C, nowMs: 100));
        Assert.Equal(2, slots.HighWater);
    }

    [Fact]
    public void ReclaimRevivesARetiredSlotUntilItIsRentedToAnotherObject()
    {
        var slots = Create(grace: 100);
        var a = slots.GetOrAdd(A, nowMs: 0);
        slots.Retire(A, nowMs: 0);
        Assert.False(slots.TryGetSlot(A, out _));

        // A late publish puts the object back on its slot.
        Assert.True(slots.Reclaim(A, a));
        Assert.True(slots.IsLive(a));
        Assert.True(slots.TryGetSlot(A, out var found));
        Assert.Equal(a, found);

        // Rented out meanwhile: refused, and the other object keeps the slot.
        slots.Retire(A, nowMs: 0);
        Assert.Equal(a, slots.GetOrAdd(B, nowMs: 100));
        Assert.False(slots.Reclaim(A, a));
        Assert.Equal(B, slots.OwnerOf(a));
    }

    [Fact]
    public void PublishKeepsTheSetRetiresTheRestAndReportsConflicts()
    {
        var slots = Create(grace: 100);
        var a = slots.GetOrAdd(A, nowMs: 0);
        var b = slots.GetOrAdd(B, nowMs: 0);
        _ = slots.GetOrAdd(C, nowMs: 0);

        var conflicts = slots.Publish([new(A, a), new(B, b)], nowMs: 5);

        Assert.Equal(0, conflicts);
        Assert.True(slots.IsLive(a));
        Assert.True(slots.IsLive(b));
        Assert.False(slots.TryGetSlot(C, out _));

        // An object whose slot another one holds is a conflict, not a share.
        Assert.Equal(1, slots.Publish([new(A, a), new(C, a)], nowMs: 6));
        Assert.Equal(A, slots.OwnerOf(a));
    }

    [Fact]
    public void ReadsAreLockFreeAndTolerateSlotsBeyondTheTable()
    {
        var slots = Create();

        Assert.False(slots.IsLive(5_000));
        Assert.Equal(Guid.Empty, slots.OwnerOf(5_000));
        Assert.False(slots.IsLive(-1));
    }

    [Fact]
    public void TheTableGrowsPastItsFirstSixteenSlotsWithOwnersIntact()
    {
        var slots = Create();
        Guid[] ids = [.. Enumerable.Range(1, 100).Select(i => new Guid(i, 0, 0x7000, 0x80, 0, 0, 0, 0, 0, 0, 1))];

        var assigned = ids.Select(id => slots.GetOrAdd(id, nowMs: 0)).ToArray();

        Assert.Equal(Enumerable.Range(0, 100), assigned);
        for (var i = 0; i < ids.Length; i++)
        {
            Assert.Equal(ids[i], slots.OwnerOf(i));
            Assert.True(slots.IsLive(i));
        }
    }
}
