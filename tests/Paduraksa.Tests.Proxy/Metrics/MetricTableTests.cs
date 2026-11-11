using Paduraksa.Proxy.Metrics;

namespace Paduraksa.Tests.Proxy.Metrics;

public sealed class MetricTableTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 4)]
    [InlineData(4, 4)]
    [InlineData(20, 16)]
    [InlineData(256, 16)]
    public void StripesFollowTheCoresUpToACap(int processors, int stripes) => Assert.Equal(stripes, MetricTable.StripesFor(processors));

    [Fact]
    public void AStrideMustBeWholeCacheLinesAndStripesAPowerOfTwo()
    {
        Assert.Throws<ArgumentException>(() => new MetricTable(12, 4));
        Assert.Throws<ArgumentException>(() => new MetricTable(8, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetricTable(0, 4));
    }

    [Fact]
    public void StripesSumAndSlotsStayApart()
    {
        MetricTable table = new(stride: 8, stripes: 4);
        table.Ensure(0);
        table.Ensure(20);

        for (var stripe = 0; stripe < 4; stripe++)
        {
            table.Increment(stripe, slot: 0, metric: 1);
            table.Add(stripe, slot: 0, metric: 2, delta: 10);
            table.Add(stripe, slot: 20, metric: 1, delta: 100);
        }

        Assert.Equal(4, table.Sum(0, 1));
        Assert.Equal(40, table.Sum(0, 2));
        Assert.Equal(400, table.Sum(20, 1));
        Assert.Equal(0, table.Sum(0, 0));
        Assert.Equal(0, table.Sum(1, 1));

        Span<long> all = stackalloc long[8];
        table.SumInto(0, all);
        Assert.Equal([0, 4, 40, 0, 0, 0, 0, 0], all.ToArray());
    }

    [Fact]
    public void ASlotWithoutRoomCountsNothingAndReadsZero()
    {
        MetricTable table = new(stride: 8, stripes: 2);

        table.Increment(0, slot: 500, metric: 0);

        Assert.Equal(0, table.Sum(500, 0));
    }

    [Fact]
    public void ClearZeroesEveryStripeOfOneSlotOnly()
    {
        MetricTable table = new(stride: 8, stripes: 2);
        table.Ensure(3);
        for (var stripe = 0; stripe < 2; stripe++)
        {
            table.Increment(stripe, 3, 0);
            table.Increment(stripe, 4, 0);
        }

        table.Clear(3);

        Assert.Equal(0, table.Sum(3, 0));
        Assert.Equal(2, table.Sum(4, 0));
    }

    [Fact]
    public async Task ConcurrentWritersOnEveryCoreLoseNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        MetricTable table = new(stride: 8, stripes: MetricTable.StripesFor(Environment.ProcessorCount));
        table.Ensure(0);
        const int Threads = 8;
        const int Each = 50_000;

        await Task.WhenAll(Enumerable.Range(0, Threads).Select(_ => Task.Run(
            () =>
            {
                for (var i = 0; i < Each; i++)
                {
                    table.Increment(table.CurrentStripe(), 0, 0);
                    table.Add(table.CurrentStripe(), 0, 1, 3);
                }
            },
            ct)));

        Assert.Equal(Threads * Each, table.Sum(0, 0));
        Assert.Equal(Threads * Each * 3L, table.Sum(0, 1));
    }
}
