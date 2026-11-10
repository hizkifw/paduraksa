using Paduraksa.Proxy.RateLimiting;

namespace Paduraksa.Tests.Proxy.RateLimiting;

public sealed class RateLimitParametersTests
{
    private const ushort Slot = 1;
    private const ulong Hash = 0x00AB_CDEF_0123;

    [Fact]
    public void AHigherRateAppliesOnTheNextTouch()
    {
        RateLimitTable table = new(64);
        RateLimitParameters slow = new(1, 10);

        Assert.Equal(10, Take(table, slow, nowMs: 0));

        // One second at the old rate is one token; at the new one it is ten.
        Assert.Equal(1, Take(table, slow, nowMs: 1_000));
        Assert.Equal(10, Take(table, new(10, 10), nowMs: 2_000));
    }

    [Fact]
    public void AHigherBurstAppliesOnTheNextTouch()
    {
        RateLimitTable table = new(64);
        RateLimitParameters small = new(1, 2);

        Assert.Equal(2, Take(table, small, nowMs: 0));
        Assert.False(table.TryAcquire(Slot, Hash, small, nowMs: 0, out _));

        // One second's refill at ten a second is ten tokens, and the bucket can now hold five of them.
        Assert.Equal(5, Take(table, new(10, 5), nowMs: 1_000));
    }

    [Fact]
    public void ALowerBurstClampsTheTokens()
    {
        RateLimitTable table = new(64);
        RateLimitParameters ten = new(1, 10);

        Assert.Equal(3, TakeSome(table, ten, nowMs: 0, count: 3));

        // Seven tokens are left; the burst drops to five, so the next touch clamps to five and spends one of them.
        RateLimitParameters five = new(1, 5);
        Assert.True(table.TryAcquire(Slot, Hash, five, nowMs: 0, out _));
        Assert.Equal(4, Take(table, five, nowMs: 0));
    }

    [Fact]
    public void ALowerRateSlowsTheRefill()
    {
        RateLimitTable table = new(64);
        RateLimitParameters fast = new(10, 10);

        Assert.Equal(10, Take(table, fast, nowMs: 0));

        // 100 ms at ten a second was a token; at one a second it is a sixth of one.
        RateLimitParameters slow = new(1, 10);
        Assert.False(table.TryAcquire(Slot, Hash, slow, nowMs: 100, out _));
        Assert.True(table.TryAcquire(Slot, Hash, slow, nowMs: 1_000, out _));
    }

    [Fact]
    public void ASlowPolicyIsAccepted()
    {
        // One token a minute is a plausible "one per minute" policy. The rate is stored in 1/16384-of-a-token steps, so
        // a minute's refill comes a few milliseconds late rather than never.
        RateLimitTable table = new(64);
        RateLimitParameters perMinute = new(1.0 / 60, 1);

        Assert.True(table.TryAcquire(Slot, Hash, perMinute, nowMs: 0, out _));
        Assert.False(table.TryAcquire(Slot, Hash, perMinute, nowMs: 59_000, out _));
        Assert.True(table.TryAcquire(Slot, Hash, perMinute, nowMs: 61_000, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.000_01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(RateLimitParameters.MaxTokensPerSecond * 2)]
    public void RejectsAnImpossibleRate(double rate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = new RateLimitParameters(rate, 1);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(RateLimitParameters.MaxBurst + 1)]
    public void RejectsAnImpossibleBurst(int burst)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = new RateLimitParameters(1, burst);
        });
    }

    [Fact]
    public void DefaultIsNoPolicy()
    {
        Assert.Equal(0, default(RateLimitParameters).Burst);
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

    private static int TakeSome(RateLimitTable table, in RateLimitParameters parameters, long nowMs, int count)
    {
        var taken = 0;
        while (taken < count && table.TryAcquire(Slot, Hash, parameters, nowMs, out _))
        {
            taken++;
        }

        return taken;
    }
}
