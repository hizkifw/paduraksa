using Paduraksa.Proxy.Metrics;

namespace Paduraksa.Tests.Proxy.Metrics;

public sealed class LatencyBucketsTests
{
    [Fact]
    public void EveryValueLandsInTheBucketWhoseBoundsHoldIt()
    {
        var previous = -1;
        // Every microsecond up to 100 ms, then a geometric walk to past the overflow point.
        for (long v = 0; v < 100_000; v++)
        {
            previous = Check(v, previous);
        }

        for (var v = 100_000L; v < (1L << 27); v += (v / 97) + 1)
        {
            previous = Check(v, previous);
        }

        static int Check(long v, int previous)
        {
            var index = LatencyBuckets.IndexOf(v);
            Assert.InRange(index, previous, LatencyBuckets.Count - 1);
            Assert.True(LatencyBuckets.LowerBound(index) <= v, $"{v} is below bucket {index}'s lower bound {LatencyBuckets.LowerBound(index)}");
            Assert.True(v < LatencyBuckets.UpperBound(index), $"{v} is not below bucket {index}'s upper bound {LatencyBuckets.UpperBound(index)}");
            return index;
        }
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(7, 7)]
    [InlineData(8, 8)]
    public void TheSmallValuesHaveBucketsOfTheirOwn(long micros, int index) => Assert.Equal(index, LatencyBuckets.IndexOf(micros));

    [Fact]
    public void SixtySecondsIsInRangeAndTheOverflowStartsAtTwoToTheTwentySixthMicroseconds()
    {
        Assert.True(LatencyBuckets.IndexOf(60_000_000) < LatencyBuckets.Overflow);
        Assert.Equal(LatencyBuckets.Overflow - 1, LatencyBuckets.IndexOf((1L << 26) - 1));
        Assert.Equal(LatencyBuckets.Overflow, LatencyBuckets.IndexOf(1L << 26));
        Assert.Equal(LatencyBuckets.Overflow, LatencyBuckets.IndexOf(long.MaxValue));
        Assert.Equal(long.MaxValue, LatencyBuckets.UpperBound(LatencyBuckets.Overflow));
    }

    [Fact]
    public void ABucketIsAtMostAQuarterOfItsLowerBoundWide()
    {
        for (var i = 4; i < LatencyBuckets.Overflow; i++)
        {
            var width = LatencyBuckets.UpperBound(i) - LatencyBuckets.LowerBound(i);
            Assert.True(width * 4 <= LatencyBuckets.LowerBound(i), $"bucket {i} is {width} wide at {LatencyBuckets.LowerBound(i)}");
        }
    }

    [Fact]
    public void TheStrideIsAWholeNumberOfCacheLines()
    {
        Assert.Equal(0, LatencyBuckets.Stride % 8);
        Assert.True(LatencyBuckets.Stride >= LatencyBuckets.Count);
        Assert.InRange(LatencyBuckets.Count, 100, 120);
    }

    [Fact]
    public void QuantilesInterpolateInsideTheBucketThatHoldsThem()
    {
        var counts = new long[LatencyBuckets.Stride];
        counts[LatencyBuckets.IndexOf(1_000)] = 90;
        counts[LatencyBuckets.IndexOf(100_000)] = 10;

        var p50 = LatencyBuckets.Quantile(counts, 0.5);
        var p95 = LatencyBuckets.Quantile(counts, 0.95);

        Assert.InRange(p50, LatencyBuckets.LowerBound(LatencyBuckets.IndexOf(1_000)), LatencyBuckets.UpperBound(LatencyBuckets.IndexOf(1_000)));
        Assert.InRange(p95, LatencyBuckets.LowerBound(LatencyBuckets.IndexOf(100_000)), LatencyBuckets.UpperBound(LatencyBuckets.IndexOf(100_000)));
        Assert.Equal(0, LatencyBuckets.Quantile(new long[LatencyBuckets.Stride], 0.99));
    }

    [Fact]
    public void TheOverflowBucketReportsItsLowerBound()
    {
        var counts = new long[LatencyBuckets.Stride];
        counts[LatencyBuckets.Overflow] = 5;

        Assert.Equal(1L << LatencyBuckets.OverflowOctave, LatencyBuckets.Quantile(counts, 0.99));
    }

    [Fact]
    public void CumulativeCountsAtAnOctaveBoundaryAreExact()
    {
        var counts = new long[LatencyBuckets.Stride];
        counts[LatencyBuckets.IndexOf(100)]++;
        counts[LatencyBuckets.IndexOf(127)]++;
        counts[LatencyBuckets.IndexOf(128)]++;
        counts[LatencyBuckets.IndexOf(5_000)]++;

        Assert.Equal(2, LatencyBuckets.CountBelowOctave(counts, 7));
        Assert.Equal(3, LatencyBuckets.CountBelowOctave(counts, 8));
        Assert.Equal(4, LatencyBuckets.CountBelowOctave(counts, 13));
        Assert.Equal(4, LatencyBuckets.CountBelowOctave(counts, LatencyBuckets.OverflowOctave));
    }
}
