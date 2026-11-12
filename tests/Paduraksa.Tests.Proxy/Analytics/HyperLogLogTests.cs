using Paduraksa.Proxy.Analytics;

namespace Paduraksa.Tests.Proxy.Analytics;

/// <summary>The visitors sketch: its error against the true count, the union a merge makes, and its stored form.</summary>
public sealed class HyperLogLogTests
{
    // 1.04 / sqrt(4096) is 1.6 %; the bound used is four standard errors, which a correct sketch never breaks on these fixed streams.
    private const double Bound = 4 * 0.016;

    /// <summary>A well-mixed 64-bit value per index, as a salted hash would be.</summary>
    internal static ulong Mix(ulong index)
    {
        var z = index + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static HyperLogLog Of(ulong from, ulong count)
    {
        HyperLogLog sketch = new();
        for (var i = from; i < from + count; i++)
        {
            sketch.Add(Mix(i));
        }

        return sketch;
    }

    [Fact]
    public void AnEmptySketchCountsNothing()
    {
        HyperLogLog sketch = new();

        Assert.True(sketch.IsEmpty);
        Assert.Equal(0, sketch.Estimate());
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(10UL)]
    [InlineData(100UL)]
    [InlineData(1_000UL)]
    [InlineData(10_000UL)]
    [InlineData(100_000UL)]
    [InlineData(1_000_000UL)]
    public void TheEstimateIsWithinTheDocumentedBoundOfTheTruth(ulong distinct)
    {
        var estimate = Of(0, distinct).Estimate();

        Assert.InRange(estimate, distinct * (1 - Bound), (distinct * (1 + Bound)) + 1);
    }

    [Fact]
    public void ASmallCountIsNearlyExact()
    {
        Assert.InRange(Of(0, 50).Estimate(), 49, 51);
    }

    [Fact]
    public void RepeatsDoNotCount()
    {
        var sketch = Of(0, 500);
        var before = sketch.Estimate();
        for (var round = 0; round < 5; round++)
        {
            for (ulong i = 0; i < 500; i++)
            {
                sketch.Add(Mix(i));
            }
        }

        Assert.Equal(before, sketch.Estimate());
    }

    [Fact]
    public void AMergeIsTheUnionNotTheSum()
    {
        // 0..59 999 and 40 000..99 999 overlap by 20 000: the union is 100 000, the sum 120 000.
        var merged = Of(0, 60_000);
        merged.Merge(Of(40_000, 60_000));

        Assert.InRange(merged.Estimate(), 100_000 * (1 - Bound), 100_000 * (1 + Bound));
    }

    [Fact]
    public void MergingIsCommutativeAndIdempotent()
    {
        var a = Of(0, 5_000);
        var b = Of(2_500, 5_000);
        var ab = a.Clone();
        ab.Merge(b);
        var ba = b.Clone();
        ba.Merge(a);
        var again = ab.Clone();
        again.Merge(a);
        again.Merge(b);

        Assert.Equal(ab.Estimate(), ba.Estimate());
        Assert.Equal(ab.Estimate(), again.Estimate());
    }

    [Theory]
    [InlineData(5UL)]
    [InlineData(400UL)]
    [InlineData(50_000UL)]
    public void ASketchSurvivesItsStoredForm(ulong distinct)
    {
        var sketch = Of(0, distinct);
        ByteWriter writer = new();
        sketch.Write(writer);

        Assert.True(HyperLogLog.TryRead(writer.Written, out var read, out var consumed));

        Assert.Equal(writer.Length, consumed);
        Assert.Equal(sketch.Estimate(), read.Estimate());
    }

    [Fact]
    public void AQuietSketchIsStoredInAFewBytes()
    {
        ByteWriter writer = new();
        Of(0, 20).Write(writer);

        Assert.True(writer.Length < 100, $"{writer.Length} bytes for 20 visitors");
    }

    [Fact]
    public void ABlobThatIsNotASketchIsRefused()
    {
        Assert.False(HyperLogLog.TryRead([], out _, out _));
        Assert.False(HyperLogLog.TryRead([9, 1, 2, 3], out _, out _));
        Assert.False(HyperLogLog.TryRead([1, 0, 0], out _, out _));
        // A sparse list that names a register past the end, and one that claims more entries than it holds.
        Assert.False(HyperLogLog.TryRead([2, 0, 1, 0xFF, 0xFF, 5], out _, out _));
        Assert.False(HyperLogLog.TryRead([2, 0xFF, 0xFF], out _, out _));
        // A register above the longest run a 64-bit hash can have.
        Assert.False(HyperLogLog.TryRead([2, 0, 1, 0, 3, 200], out _, out _));
    }
}
