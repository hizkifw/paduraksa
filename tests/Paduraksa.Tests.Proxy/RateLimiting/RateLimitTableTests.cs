using Paduraksa.Proxy.RateLimiting;

namespace Paduraksa.Tests.Proxy.RateLimiting;

public sealed class RateLimitTableTests
{
    private const ushort Slot = 1;
    private const ulong Hash = 0x0123_4567_89AB;

    [Fact]
    public void ASampleOlderThanTheStoredTimeEarnsNoRefill()
    {
        // Rate 1000/s: one millisecond is a whole token, so any refill shows.
        RateLimitTable table = new(64);
        RateLimitParameters fast = new(1000, 10);
        Assert.Equal(10, Take(table, fast, nowMs: 105));

        // A thread that read the clock at 100 lands after the take stored 105: refused, not refilled to the burst.
        Assert.False(table.TryAcquire(Slot, Hash, fast, nowMs: 100, out var retryAfter));
        Assert.Equal(1, retryAfter);

        // Nor did it move the stored time back: at 106 exactly one token has been earned.
        Assert.Equal(1, Take(table, fast, nowMs: 106));
    }

    [Fact]
    public void DefaultParametersAreRefused() =>
        Assert.Throws<ArgumentException>(() => new RateLimitTable(64).TryAcquire(Slot, Hash, default, nowMs: 0, out _));

    [Fact]
    public void OneTokenRefillsEverySecond()
    {
        RateLimitTable table = new(64);
        RateLimitParameters onePerSecond = new(1, 10);

        Assert.Equal(10, Take(table, onePerSecond, nowMs: 0));
        Assert.False(table.TryAcquire(Slot, Hash, onePerSecond, nowMs: 0, out var retryAfter));
        Assert.Equal(1, retryAfter);

        // A whole second earns one whole token, and a millisecond less earns none of it.
        Assert.False(table.TryAcquire(Slot, Hash, onePerSecond, nowMs: 999, out _));
        Assert.True(table.TryAcquire(Slot, Hash, onePerSecond, nowMs: 1_000, out _));
        Assert.False(table.TryAcquire(Slot, Hash, onePerSecond, nowMs: 1_000, out _));
    }

    [Fact]
    public void ATokenIsMadeOfSixteenths()
    {
        RateLimitTable table = new(64);
        // 16 tokens a second is one token per 62.5 ms: 62 ms is 15/16 of a token, 63 ms is 16/16.
        RateLimitParameters sixteenPerSecond = new(16, 1);

        Assert.True(table.TryAcquire(Slot, Hash, sixteenPerSecond, nowMs: 0, out _));
        Assert.False(table.TryAcquire(Slot, Hash, sixteenPerSecond, nowMs: 62, out _));
        Assert.True(table.TryAcquire(Slot, Hash, sixteenPerSecond, nowMs: 63, out _));
        Assert.False(table.TryAcquire(Slot, Hash, sixteenPerSecond, nowMs: 63, out _));
    }

    [Theory]
    [InlineData(1, 1)] // one second: already whole
    [InlineData(0.25, 4)] // four seconds for a token
    [InlineData(0.0625, 16)] // sixteen seconds for a token
    public void RetryAfterIsTheCeilingInWholeSeconds(double rate, int seconds)
    {
        RateLimitTable table = new(64);
        RateLimitParameters parameters = new(rate, 1);

        Assert.True(table.TryAcquire(Slot, Hash, parameters, nowMs: 0, out _));
        Assert.False(table.TryAcquire(Slot, Hash, parameters, nowMs: 0, out var retryAfter));
        Assert.Equal(seconds, retryAfter);
    }

    [Fact]
    public void RetryAfterIsAtLeastOneSecond()
    {
        RateLimitTable table = new(64);
        RateLimitParameters fast = new(RateLimitParameters.MaxTokensPerSecond, 1);

        Assert.True(table.TryAcquire(Slot, Hash, fast, nowMs: 0, out _));
        Assert.False(table.TryAcquire(Slot, Hash, fast, nowMs: 0, out var retryAfter));
        Assert.Equal(1, retryAfter);
    }

    [Fact]
    public void TheRefillSaturatesAtTheBurst()
    {
        RateLimitTable table = new(64);
        RateLimitParameters threePerBurst = new(1, 3);

        Assert.Equal(3, Take(table, threePerBurst, nowMs: 0));

        // A minute is sixty tokens of refill, but the bucket only holds three.
        Assert.Equal(3, Take(table, threePerBurst, nowMs: 60_000));
        Assert.False(table.TryAcquire(Slot, Hash, threePerBurst, nowMs: 60_000, out _));
    }

    [Fact]
    public void AVeryLongIdleGapRefillsInsteadOfOverflowing()
    {
        RateLimitTable table = new(64);
        RateLimitParameters fast = new(RateLimitParameters.MaxTokensPerSecond, 4);

        Assert.Equal(4, Take(table, fast, nowMs: 0));
        Assert.Equal(4, Take(table, fast, nowMs: RateLimitTable.MaxTimeMs));
    }

    [Fact]
    public void TheTimeFieldIsFortyBitsAndItsElapsedTimeSurvivesTheWrap()
    {
        // 2^40 - 1 ms is about 34.8 years: longer than any process, so the field's wrap can be ignored.
        Assert.Equal(1_099_511_627_775, RateLimitTable.MaxTimeMs);

        RateLimitTable table = new(64);
        RateLimitParameters onePerSecond = new(1, 1);

        Assert.True(table.TryAcquire(Slot, Hash, onePerSecond, nowMs: RateLimitTable.MaxTimeMs, out _));
        Assert.False(table.TryAcquire(Slot, Hash, onePerSecond, nowMs: RateLimitTable.MaxTimeMs, out _));

        // 1000 ms later, the time of day is past the field's end; the elapsed time is still 1000 ms.
        Assert.True(table.TryAcquire(Slot, Hash, onePerSecond, nowMs: RateLimitTable.MaxTimeMs + 1_000, out _));
    }

    [Fact]
    public void SlotZeroIsNotABucket()
    {
        RateLimitTable table = new(64);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = table.TryAcquire(0, Hash, new(1, 1), nowMs: 0, out _);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(RateLimitTable.MaximumCapacity * 2)]
    public void TheCapacityMustBeAPowerOfTwoInRange(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = new RateLimitTable(capacity);
        });
    }

    private static int Take(RateLimitTable table, in RateLimitParameters parameters, long nowMs)
    {
        var taken = 0;
        while (table.TryAcquire(Slot, Hash, parameters, nowMs, out _))
        {
            taken++;
        }

        return taken;
    }
}
