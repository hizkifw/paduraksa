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
}
