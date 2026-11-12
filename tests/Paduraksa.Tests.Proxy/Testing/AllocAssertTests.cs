namespace Paduraksa.Tests.Proxy.Testing;

public sealed class AllocAssertTests
{
    [Fact]
    public void ZeroPassesForAllocationFreeCode() =>
        AllocAssert.Zero(new int[16], static a => a[0]++, "array increment");

    [Fact]
    public void MeasureSeesAllocations()
    {
        var result = AllocAssert.Measure(8, static n => GC.KeepAlive(new byte[n]), warmup: 10, iterations: 100);

        Assert.Equal(100, result.Iterations);
        Assert.Equal(0, result.FirstNonZeroIteration);
        Assert.True(result.TotalBytes >= 100 * 8);
    }

    [Fact]
    public void MeasureSettledStillSeesRepeatableAllocations() =>
        Assert.True(AllocAssert.MeasureSettled(8, static n => GC.KeepAlive(new byte[n]), warmup: 10, iterations: 100).TotalBytes >= 100 * 8);

    [Fact]
    public void ZeroFailsWhenCodeAllocates() =>
        Assert.ThrowsAny<Exception>(() =>
            AllocAssert.Zero(8, static n => GC.KeepAlive(new byte[n]), "allocating code", warmup: 10, iterations: 100));

    [Fact]
    public void ZeroForgivesOneAllocatingCallInEachRunAsTheRuntimesOwnOneOff() =>
        AllocAssert.Zero(new int[1], static calls => AllocateAt(calls, 50), "a lone one-off per run", warmup: 0, iterations: 100);

    [Fact]
    public void ZeroFailsWhenMoreThanOneCallInARunAllocates() =>
        Assert.ThrowsAny<Exception>(() =>
            AllocAssert.Zero(new int[1], static calls => AllocateAt(calls, 50, 60), "two allocating calls per run", warmup: 0, iterations: 100));

    [Fact]
    public void MeasureSettledCountsALoneAllocatingCallInTheRerunAsNone()
    {
        var result = AllocAssert.MeasureSettled(new int[1], static calls => AllocateAt(calls, 50), warmup: 0, iterations: 100);

        Assert.Equal(0, result.TotalBytes);
    }

    // Allocates on the calls whose index within each 100 is one of the two given, so each measured run of 100 sees the same number of them.
    private static void AllocateAt(int[] calls, int first, int second = -1)
    {
        var position = calls[0]++ % 100;
        if (position == first || position == second)
        {
            GC.KeepAlive(new byte[64]);
        }
    }
}
