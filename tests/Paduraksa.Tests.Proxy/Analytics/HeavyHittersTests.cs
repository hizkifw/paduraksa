using Paduraksa.Proxy.Analytics;

namespace Paduraksa.Tests.Proxy.Analytics;

/// <summary>The Space-Saving sketch behind the top tables: exact while it fits, bounded when it doesn't, and still bounded after a merge.</summary>
public sealed class HeavyHittersTests
{
    private static string Key(int rank) => "/k" + rank;

    /// <summary>A skewed stream: key <c>i</c> is drawn with weight about 1/(i+1), the way paths are.</summary>
    private static List<string> Zipf(int length, int distinct, int seed)
    {
        Random random = new(seed);
        var weights = Enumerable.Range(0, distinct).Select(static i => 1.0 / (i + 1)).ToArray();
        var cumulative = new double[distinct];
        var sum = 0.0;
        for (var i = 0; i < distinct; i++)
        {
            sum += weights[i];
            cumulative[i] = sum;
        }

        List<string> stream = new(length);
        for (var n = 0; n < length; n++)
        {
            var draw = random.NextDouble() * sum;
            var at = Array.BinarySearch(cumulative, draw);
            stream.Add(Key(at < 0 ? ~at : at));
        }

        return stream;
    }

    private static HeavyHitters Fill(IEnumerable<string> stream, int capacity)
    {
        HeavyHitters sketch = new(capacity);
        foreach (var key in stream)
        {
            sketch.Add(key, 1, -1, false, -1);
        }

        return sketch;
    }

    private static Dictionary<string, long> Truth(IEnumerable<string> stream) =>
        stream.GroupBy(static key => key).ToDictionary(static group => group.Key, static group => (long)group.Count());

    // The guarantee: for every tracked key the truth is between Count - Error and Count, and an untracked key weighs at most the floor.
    private static void AssertBounds(HeavyHitters sketch, Dictionary<string, long> truth)
    {
        var tracked = sketch.Top(int.MaxValue).ToDictionary(static hitter => hitter.Key);
        foreach (var (key, actual) in truth)
        {
            if (tracked.TryGetValue(key, out var hitter))
            {
                Assert.True(hitter.Count >= actual, $"{key}: count {hitter.Count} below the truth {actual}");
                Assert.True(hitter.Count - hitter.Error <= actual, $"{key}: lower bound {hitter.Count - hitter.Error} above the truth {actual}");
            }
            else
            {
                Assert.True(actual <= sketch.Floor, $"{key}: untracked but weighs {actual}, floor {sketch.Floor}");
            }
        }
    }

    [Fact]
    public void ItIsExactWhileEveryKeyFits()
    {
        var stream = Zipf(5_000, 40, seed: 1);
        var sketch = Fill(stream, capacity: 64);

        var truth = Truth(stream);
        Assert.Equal(0, sketch.Floor);
        foreach (var hitter in sketch.Top(100))
        {
            Assert.Equal(truth[hitter.Key], hitter.Count);
            Assert.Equal(0, hitter.Error);
            Assert.Equal(hitter.Count, hitter.Requests);
        }
    }

    [Fact]
    public void ItNeverHoldsMoreThanItsCapacity()
    {
        var sketch = Fill(Enumerable.Range(0, 10_000).Select(Key), capacity: 32);

        Assert.Equal(32, sketch.Size);
        Assert.Equal(32, sketch.Top(1_000).Count);
    }

    [Fact]
    public void TheHeavyKeysOfASkewedStreamAreAllFoundWithinTheBound()
    {
        var stream = Zipf(200_000, 5_000, seed: 2);
        var sketch = Fill(stream, capacity: 64);
        var truth = Truth(stream);

        AssertBounds(sketch, truth);
        // Tracked, not necessarily ranked alike: estimates overshoot, so two close keys can swap places inside the top.
        var found = sketch.Top(int.MaxValue).Select(static hitter => hitter.Key).ToHashSet();
        var trueTop = truth.OrderByDescending(static pair => pair.Value).Take(10).Select(static pair => pair.Key);
        Assert.All(trueTop, key => Assert.Contains(key, found));
        // Space-Saving's overshoot is at most n / capacity, so for the heaviest keys the estimate is close.
        foreach (var hitter in sketch.Top(10))
        {
            Assert.InRange(hitter.Count - truth[hitter.Key], 0, stream.Count / 64);
        }
    }

    [Fact]
    public void AKeyHeavierThanTheFloorIsAlwaysTracked()
    {
        var stream = Zipf(50_000, 2_000, seed: 3);
        var sketch = Fill(stream, capacity: 48);

        var tracked = sketch.Top(int.MaxValue).Select(static hitter => hitter.Key).ToHashSet();
        foreach (var (key, count) in Truth(stream))
        {
            if (count > sketch.Floor)
            {
                Assert.Contains(key, tracked);
            }
        }
    }

    [Fact]
    public void AMergeKeepsTheBoundOverTheWholeStream()
    {
        var stream = Zipf(120_000, 3_000, seed: 4);
        var truth = Truth(stream);
        var parts = Enumerable.Range(0, 8).Select(i => stream.Skip(i * 15_000).Take(15_000)).Select(part => Fill(part, capacity: 64)).ToList();

        // Merged into one of the same size, as the hourly roll-up does, and into a larger one, as a range read does.
        var small = new HeavyHitters(64);
        var large = new HeavyHitters(1_024);
        foreach (var part in parts)
        {
            small.MergeFrom(part);
            large.MergeFrom(part);
        }

        AssertBounds(small, truth);
        AssertBounds(large, truth);
        Assert.True(small.Size <= 64);
        // Every heavy key survives in the larger one, and its estimate is within the sum of its parts' errors.
        var top = truth.OrderByDescending(static pair => pair.Value).Take(10).ToList();
        var merged = large.Top(int.MaxValue).ToDictionary(static hitter => hitter.Key);
        foreach (var (key, count) in top)
        {
            Assert.True(merged.ContainsKey(key), key);
            Assert.InRange(merged[key].Count - count, 0, 8 * (15_000 / 64));
        }
    }

    [Fact]
    public void MergingASmallerSketchIntoAnExactOneStaysExact()
    {
        HeavyHitters a = new(16);
        HeavyHitters b = new(16);
        a.Add("/x", 1, -1, false, -1);
        a.Add("/x", 1, -1, false, -1);
        b.Add("/x", 1, -1, false, -1);
        b.Add("/y", 1, -1, false, -1);

        a.MergeFrom(b);

        var top = a.Top(5);
        Assert.Equal(("/x", 3L, 0L), (top[0].Key, top[0].Count, top[0].Error));
        Assert.Equal(("/y", 1L, 0L), (top[1].Key, top[1].Count, top[1].Error));
        Assert.Equal(0, a.Floor);
    }

    [Fact]
    public void WeightsRankByBytesAndThePayloadCountsRequests()
    {
        HeavyHitters sketch = new(8, keepsLatency: true);
        sketch.Add("/small", 1_000, 1_000, false, 1_000);
        sketch.Add("/small", 1_000, 1_000, true, 2_000);
        sketch.Add("/large", 5_000_000, 5_000_000, false, 400_000);

        var top = sketch.Top(2);

        Assert.Equal("/large", top[0].Key);
        Assert.Equal(5_000_000, top[0].Bytes);
        Assert.Equal(1, top[0].Requests);
        Assert.Equal(2, top[1].Requests);
        Assert.Equal(1, top[1].ServerErrors);
        Assert.InRange(top[0].P95Micros, 300_000, 500_000);
    }

    [Fact]
    public void AnEvictedKeyStartsItsPayloadOverButKeepsTheBound()
    {
        HeavyHitters sketch = new(2, keepsLatency: true);
        sketch.Add("/a", 1, 10, false, 100);
        sketch.Add("/a", 1, 10, false, 100);
        sketch.Add("/b", 1, 10, false, 100);

        sketch.Add("/c", 1, 10, false, 100);

        var c = sketch.Top(10).Single(hitter => hitter.Key == "/c");
        Assert.Equal(2, c.Count);
        Assert.Equal(1, c.Error);
        Assert.Equal(1, c.Requests);
        Assert.Equal(1, sketch.Floor);
    }

    [Fact]
    public void ASketchSurvivesItsStoredForm()
    {
        var sketch = Fill(Zipf(20_000, 500, seed: 5), capacity: 64);
        sketch.MergeFrom(Fill(Zipf(5_000, 500, seed: 6), capacity: 64));
        ByteWriter writer = new();
        sketch.Write(writer);

        ByteReader reader = new(writer.Written);
        var read = HeavyHitters.Read(ref reader);

        Assert.NotNull(read);
        Assert.Equal(0, reader.Remaining);
        Assert.Equal(sketch.Floor, read.Floor);
        Assert.Equal(sketch.Top(100), read.Top(100), new HitterComparer());
    }

    [Fact]
    public void AStoredLatencyHistogramComesBack()
    {
        HeavyHitters sketch = new(4, keepsLatency: true);
        for (var i = 1; i <= 100; i++)
        {
            sketch.Add("/slow", 1, -1, false, i * 1_000);
        }

        ByteWriter writer = new();
        sketch.Write(writer);
        ByteReader reader = new(writer.Written);
        var read = HeavyHitters.Read(ref reader)!;

        Assert.Equal(sketch.Top(1)[0].P95Micros, read.Top(1)[0].P95Micros);
        Assert.InRange(read.Top(1)[0].P95Micros, 85_000, 105_000);
    }

    [Fact]
    public void ACorruptBlobIsRefusedAtEveryCut()
    {
        var sketch = Fill(Zipf(2_000, 100, seed: 7), capacity: 16);
        ByteWriter writer = new();
        sketch.Write(writer);
        var bytes = writer.Written.ToArray();

        for (var length = 0; length < bytes.Length; length++)
        {
            ByteReader reader = new(bytes.AsSpan(0, length));
            Assert.Null(HeavyHitters.Read(ref reader));
        }

        // A size that claims more keys than the capacity.
        ByteReader liar = new([4, 0, 0, 9]);
        Assert.Null(HeavyHitters.Read(ref liar));
    }

    [Fact]
    public void ACloneSharesNothing()
    {
        var sketch = new HeavyHitters(4, keepsLatency: true);
        sketch.Add("/a", 1, 5, false, 500);
        var clone = sketch.Clone();

        sketch.Add("/a", 1, 5, false, 500);

        Assert.Equal(1, clone.Top(1)[0].Count);
        Assert.Equal(2, sketch.Top(1)[0].Count);
    }

    private sealed class HitterComparer : IEqualityComparer<HeavyHitter>
    {
        public bool Equals(HeavyHitter? x, HeavyHitter? y) =>
            x is not null && y is not null && (x.Key, x.Count, x.Error, x.Requests, x.Bytes, x.ServerErrors) == (y.Key, y.Count, y.Error, y.Requests, y.Bytes, y.ServerErrors);

        public int GetHashCode(HeavyHitter obj) => obj.Key.GetHashCode(StringComparison.Ordinal);
    }
}
