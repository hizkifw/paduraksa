using Paduraksa.Proxy.RateLimiting;

namespace Paduraksa.Tests.Proxy.RateLimiting;

/// <summary>
/// The process runtime: slots stay put for one (policy, host) identity, a publish swaps the live parameters and retires
/// the pairs the new snapshot drops so their buckets can be cleared, and a pair retired then re-added never reads stale
/// tokens (PLAN §6.3).
/// </summary>
public sealed class RateLimitRuntimeTests
{
    [Fact]
    public void OneIdentityKeepsItsSlotAndAnotherGetsAnother()
    {
        RateLimitRuntime runtime = new(new RateLimitTable(64));

        var app = runtime.SlotFor("rlp_one/host_app");
        Assert.NotEqual(0, app);
        Assert.Equal(app, runtime.SlotFor("rlp_one/host_app"));
        Assert.NotEqual(app, runtime.SlotFor("rlp_one/host_other"));
        Assert.NotEqual(app, runtime.SlotFor("rlp_two/host_app"));
    }

    [Fact]
    public void PublishMakesTheParametersLiveAndRetiresThePairsThatAreGone()
    {
        RateLimitRuntime runtime = new(new RateLimitTable(64));
        var app = runtime.SlotFor("rlp_one/host_app");
        var api = runtime.SlotFor("rlp_one/host_api");
        RateLimitParameters first = new(10, 20);
        RateLimitParameters second = new(5, 8);

        runtime.Publish([new RateLimitRegistration(app, first, "rlp_one/host_app"), new RateLimitRegistration(api, second, "rlp_one/host_api")]);

        Assert.True(runtime.Slots.IsLive(app));
        Assert.True(runtime.Slots.IsLive(api));
        Assert.True(runtime.Slots.Parameters.TryGet(app, out var published));
        Assert.Equal(first, published);
        Assert.True(runtime.Slots.Parameters.TryGet(api, out published));
        Assert.Equal(second, published);

        // The next snapshot has only the app pair: the API pair stops being live, so the sweeper clears its buckets.
        RateLimitParameters edited = new(1, 2);
        runtime.Publish([new RateLimitRegistration(app, edited, "rlp_one/host_app")]);

        Assert.True(runtime.Slots.IsLive(app));
        Assert.False(runtime.Slots.IsLive(api));
        Assert.True(runtime.Slots.Parameters.TryGet(app, out published));
        Assert.Equal(edited, published);
    }

    [Fact]
    public void ARetiredPairLetsTheSweeperClearItsBuckets()
    {
        RateLimitRuntime runtime = new(new RateLimitTable(64));
        var slot = runtime.SlotFor("rlp_one/host_app");
        RateLimitParameters parameters = new(0.001, 10);
        runtime.Publish([new RateLimitRegistration(slot, parameters, "rlp_one/host_app")]);

        Assert.True(runtime.Table.TryAcquire(slot, partitionHash: 42, in parameters, nowMs: RateLimitClock.NowMs, out _));

        // A snapshot without the pair retires it; the sweeper clears a non-live slot at once.
        runtime.Publish([]);
        Assert.False(runtime.Slots.IsLive(slot));
        Assert.True(new RateLimitSweeper(runtime.Table, runtime.Slots).Sweep(RateLimitClock.NowMs) >= 1);
    }

    [Fact]
    public void AnEmptyPublishRetiresEverythingItPublishedBefore()
    {
        RateLimitRuntime runtime = new(new RateLimitTable(64));
        var slot = runtime.SlotFor("rlp_one/host_app");
        runtime.Publish([new RateLimitRegistration(slot, new(1, 1), "rlp_one/host_app")]);
        Assert.True(runtime.Slots.IsLive(slot));

        runtime.Publish([]);
        Assert.False(runtime.Slots.IsLive(slot));
        Assert.False(runtime.Slots.Parameters.TryGet(slot, out _));
    }

    [Fact]
    public void AReAddedIdentityGetsAFreshSlotAndNeverItsStaleTokens()
    {
        RateLimitRuntime runtime = new(new RateLimitTable(64));
        RateLimitParameters parameters = new(0.001, 4);
        var first = runtime.SlotFor("rlp_one/host_app");
        Assert.Equal(4, Take(runtime, first, parameters, RateLimitClock.NowMs));

        // Retired inside the grace period: the identity is gone, so a re-add must not come back to these buckets.
        Assert.True(runtime.Slots.Retire("rlp_one/host_app", RateLimitClock.NowMs));
        var second = runtime.SlotFor("rlp_one/host_app");

        Assert.NotEqual(first, second);
        Assert.False(runtime.Slots.IsLive(first));
        Assert.Equal(4, Take(runtime, second, parameters, RateLimitClock.NowMs));
    }

    [Fact]
    public void AnOlderPublishThatRetiredAPairCannotUndoTheNewerPublishThatReAddedIt()
    {
        RateLimitRuntime runtime = new(new RateLimitTable(64));
        var pair = runtime.SlotFor("rlp_one/host_app");
        var other = runtime.SlotFor("rlp_two/host_app");
        RateLimitParameters parameters = new(0.001, 1);
        RateLimitRegistration reAdded = new(pair, parameters, "rlp_one/host_app");
        RateLimitRegistration untouched = new(other, parameters, "rlp_two/host_app");

        runtime.Publish([reAdded, untouched]);

        // An older candidate removes the pair and swaps after a newer one re-added it. The newer publish reclaims the
        // pair's slot, so the live snapshot's buckets are not swept as if the pair were retired.
        runtime.Publish([untouched]);
        Assert.False(runtime.Slots.IsLive(pair));

        runtime.Publish([reAdded, untouched]);
        Assert.True(runtime.Slots.IsLive(pair));

        Assert.True(runtime.Table.TryAcquire(pair, partitionHash: 7, in parameters, nowMs: 0, out _));
        _ = new RateLimitSweeper(runtime.Table, runtime.Slots).Sweep(nowMs: 0);
        Assert.False(runtime.Table.TryAcquire(pair, partitionHash: 7, in parameters, nowMs: 0, out _));
    }

    [Fact]
    public void APublishWhoseSlotBelongsToAnotherIdentityFailsAndLeavesItsParametersOut()
    {
        RateLimitRuntime runtime = new(new RateLimitTable(64));
        var slot = runtime.SlotFor("rlp_one/host_app");
        RateLimitParameters parameters = new(0.001, 1);
        runtime.Publish([new RateLimitRegistration(slot, parameters, "rlp_one/host_app")]);
        runtime.Publish([]);

        // Stand in for a rent: another identity takes the retired slot, so the pair can no longer have it back.
        Assert.True(runtime.Slots.Reclaim("rlp_two/host_other", slot, RateLimitClock.NowMs));
        var conflicts = runtime.Publish([new RateLimitRegistration(slot, parameters, "rlp_one/host_app")]);

        Assert.Equal(1, conflicts);
        Assert.True(runtime.Slots.IsLive(slot));
        Assert.False(runtime.Slots.Parameters.TryGet(slot, out _));
    }

    private static int Take(RateLimitRuntime runtime, ushort slot, in RateLimitParameters parameters, long nowMs) =>
        Take(runtime.Table, slot, parameters, nowMs);

    private static int Take(RateLimitTable table, ushort slot, in RateLimitParameters parameters, long nowMs)
    {
        var taken = 0;
        while (table.TryAcquire(slot, partitionHash: 7, in parameters, nowMs, out _))
        {
            taken++;
        }

        return taken;
    }
}
