using Paduraksa.Vault.Passwords;

namespace Paduraksa.Tests.Vault.Passwords;

public sealed class PasswordVerificationCacheTests
{
    private const long Minute = 60_000;

    [Fact]
    public void AKeyIsFoundUntilItExpires()
    {
        PasswordVerificationCache cache = new(16);

        cache.Add(42, nowMs: 1000);

        Assert.True(cache.Contains(42, 1000));
        Assert.True(cache.Contains(42, 1000 + PasswordVerificationCache.LifetimeMs - 1));
        Assert.False(cache.Contains(42, 1000 + PasswordVerificationCache.LifetimeMs));
        Assert.False(cache.Contains(43, 1000));
    }

    [Fact]
    public void AHitPushesTheExpiryToFiveMinutesFromNow()
    {
        PasswordVerificationCache cache = new(16);
        cache.Add(42, 0);

        Assert.True(cache.TryHit(42, 4 * Minute + 30_000));

        Assert.True(cache.Contains(42, 9 * Minute + 29_000));
        Assert.False(cache.Contains(42, 9 * Minute + 30_000));
    }

    [Fact]
    public void AHitWithMoreThanFourMinutesLeftWritesNothing()
    {
        PasswordVerificationCache cache = new(16);
        cache.Add(42, 0);

        Assert.True(cache.TryHit(42, 30_000));
        Assert.True(cache.TryHit(42, Minute - 1));

        // Never extended: the entry still ends five minutes after it was added.
        Assert.True(cache.Contains(42, 5 * Minute - 1));
        Assert.False(cache.Contains(42, 5 * Minute));

        // One minute in, exactly four are left: still not rewritten; a millisecond later it is.
        Assert.True(cache.TryHit(42, Minute));
        Assert.False(cache.Contains(42, 5 * Minute));
        Assert.True(cache.TryHit(42, Minute + 1));
        Assert.True(cache.Contains(42, 6 * Minute));
    }

    [Fact]
    public void AnExpiredOrAbsentKeyIsNotBroughtBackByAHit()
    {
        PasswordVerificationCache cache = new(16);
        cache.Add(42, 0);

        Assert.False(cache.TryHit(42, 5 * Minute));
        Assert.False(cache.Contains(42, 5 * Minute + 1));
        Assert.False(cache.TryHit(43, 0));
        Assert.Equal(0, cache.LiveCount(5 * Minute));
    }

    [Fact]
    public void AnIdleEntryDropsOutBetweenFourAndFiveMinutesAfterItsLastUse()
    {
        PasswordVerificationCache cache = new(16);
        cache.Add(42, 0);

        // Used every ten seconds for ten minutes: always live.
        for (long now = 10_000; now <= 10 * Minute; now += 10_000)
        {
            Assert.True(cache.TryHit(42, now));
        }

        // The last use was at 10:00 and rewrote at 9:00:10 at the latest, so it ends by 14:00:10 and not before 14:00.
        Assert.True(cache.Contains(42, 10 * Minute + 3 * Minute + 59_000));
        Assert.False(cache.Contains(42, 10 * Minute + 5 * Minute));
    }

    [Fact]
    public void AddingAKeyAgainRenewsItInPlace()
    {
        PasswordVerificationCache cache = new(16);

        cache.Add(42, 0);
        cache.Add(42, 4 * Minute);

        Assert.True(cache.Contains(42, 6 * Minute));
        Assert.Equal(1, cache.LiveCount(6 * Minute));
    }

    [Fact]
    public void AFullNeighbourhoodEvictsTheEntryThatWouldLapseFirstAndKeepsTheTableBounded()
    {
        PasswordVerificationCache cache = new(4);

        // Capacity 4 and four probes: every key shares one neighbourhood.
        cache.Add(1, 0);
        cache.Add(2, 10);
        cache.Add(3, 20);
        cache.Add(4, 30);
        cache.Add(5, 40);

        Assert.False(cache.Contains(1, 50));
        Assert.True(cache.Contains(5, 50));
        Assert.Equal(4, cache.LiveCount(50));
    }

    [Fact]
    public void AnExpiredEntryIsReusedBeforeALiveOneIsGivenUp()
    {
        PasswordVerificationCache cache = new(4);
        cache.Add(1, 0);
        cache.Add(2, PasswordVerificationCache.LifetimeMs);
        cache.Add(3, PasswordVerificationCache.LifetimeMs);
        cache.Add(4, PasswordVerificationCache.LifetimeMs);

        var now = PasswordVerificationCache.LifetimeMs + 1;
        cache.Add(5, now);

        Assert.True(cache.Contains(2, now));
        Assert.True(cache.Contains(3, now));
        Assert.True(cache.Contains(4, now));
        Assert.True(cache.Contains(5, now));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(0)]
    public void ACapacityThatIsNotAPowerOfTwoIsRefused(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PasswordVerificationCache(capacity));
    }

    [Fact]
    public async Task ConcurrentRefreshesAndWritersNeverMakeAnUnaddedKeyLive()
    {
        PasswordVerificationCache cache = new(64);
        using CancellationTokenSource stop = new();
        var forged = 0;
        var writers = Enumerable.Range(0, 2).Select(w => Task.Run(() =>
        {
            for (ulong i = 1; !stop.IsCancellationRequested; i++)
            {
                cache.Add((ulong)w * 1_000_000 + (i % 200) + 1, 1000);
            }
        }, TestContext.Current.CancellationToken)).ToArray();
        var refreshers = Enumerable.Range(0, 2).Select(r => Task.Run(() =>
        {
            for (var i = 0; i < 1_000_000; i++)
            {
                // Hits on keys the writers do produce, so the slots are rewritten; keys nobody adds must never turn up live.
                cache.TryHit((ulong)(i % 200) + 1, 1000 + 4 * Minute);
                if (cache.TryHit(9_000_000 + (ulong)(i % 1000), 1000 + 4 * Minute))
                {
                    Interlocked.Increment(ref forged);
                }
            }
        }, TestContext.Current.CancellationToken)).ToArray();

        await Task.WhenAll(refreshers);
        await stop.CancelAsync();
        await Task.WhenAll(writers);

        Assert.Equal(0, forged);
    }

    [Fact]
    public async Task ConcurrentWritersAndReadersNeverBelieveAKeyNobodyAdded()
    {
        PasswordVerificationCache cache = new(64);
        using CancellationTokenSource stop = new();
        var forged = 0;
        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            for (ulong i = 1; !stop.IsCancellationRequested; i++)
            {
                cache.Add((ulong)w * 1_000_000 + (i % 500) + 1, 1000);
            }
        }, TestContext.Current.CancellationToken)).ToArray();
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 2_000_000; i++)
            {
                // Keys no writer produces: a torn key and expiry would show up here as a hit.
                if (cache.Contains(9_000_000 + (ulong)(i % 1000), 1001))
                {
                    Interlocked.Increment(ref forged);
                }
            }
        }, TestContext.Current.CancellationToken);

        await reader;
        await stop.CancelAsync();
        await Task.WhenAll(writers);

        Assert.Equal(0, forged);
    }
}
