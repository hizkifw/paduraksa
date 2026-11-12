using Paduraksa.Proxy.Metrics;

namespace Paduraksa.Tests.Proxy.Metrics;

public sealed class TickSplitTests
{
    private const long Bucket = 10_000;

    [Theory]
    [InlineData(1_000, 2_000, 1)]
    [InlineData(9_000, 11_000, 2)]
    [InlineData(9_000, 10_000, 1)]
    [InlineData(5_000, 30_000, 3)]
    [InlineData(5_000, 3_605_000, 361)]
    public void AnIntervalTouchesTheBucketsItOverlaps(long start, long end, int segments)
    {
        var split = TickSplit.Of(start, end, Bucket);

        Assert.Equal(segments, split.Segments);
        Assert.Equal(start / Bucket, split.FirstBucket);
        Assert.Equal(end - start, Enumerable.Range(0, split.Segments).Sum(split.Millis));
    }

    [Fact]
    public void TheSharesOfAnyGainAddUpToItExactlyAndStayWithinACountOfTheirProportion()
    {
        Random random = new(11);
        for (var round = 0; round < 500; round++)
        {
            var start = random.NextInt64(0, 100_000);
            var end = start + random.NextInt64(1, 400_000);
            var gain = random.NextInt64(0, 5_000_000_000);
            var split = TickSplit.Of(start, end, Bucket);

            var shares = Enumerable.Range(0, split.Segments).Select(k => split.Share(gain, k)).ToArray();

            Assert.Equal(gain, shares.Sum());
            for (var k = 0; k < shares.Length; k++)
            {
                Assert.InRange(shares[k], (gain * (double)split.Millis(k) / (end - start)) - 1, (gain * (double)split.Millis(k) / (end - start)) + 1);
            }
        }
    }

    [Fact]
    public void AnIntervalOfNoLengthPutsItsWholeGainInOnePieceThatObservedNothing()
    {
        var split = TickSplit.None(25_000, Bucket);

        Assert.Equal(1, split.Segments);
        Assert.Equal(2, split.FirstBucket);
        Assert.Equal(0, split.Millis(0));
        Assert.Equal(7, split.Share(7, 0));
    }
}
