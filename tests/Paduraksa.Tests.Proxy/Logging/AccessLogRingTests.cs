using System.Globalization;

using Paduraksa.Proxy.Logging;

namespace Paduraksa.Tests.Proxy.Logging;

public sealed class AccessLogRingTests
{
    [Fact]
    public void EntriesComeOutInOrder()
    {
        AccessLogRing ring = new(8);
        for (int i = 0; i < 8; i++)
        {
            Assert.True(ring.TryWrite(Entry(i)));
        }

        for (int i = 0; i < 8; i++)
        {
            Assert.True(ring.TryRead(out AccessLogEntry entry));
            Assert.Equal(Path(i), entry.Path);
        }

        Assert.False(ring.TryRead(out _));
        Assert.Equal(8, ring.EntriesWritten);
        Assert.Equal(8, ring.EntriesRead);
        Assert.Equal(0, ring.Dropped);
    }

    [Fact]
    public void ASlotIsAWholeNumberOfCacheLines()
    {
        // Neighbouring slots must not share a line: producers write different slots, and a shared line would ping-pong
        // between cores. 64-byte lines (x86-64 and ARM64), so a slot is padded to 192 bytes and a test pins it.
        Assert.Equal(AccessLogRing.SlotBytes, AccessLogRing.SlotSizeBytes);
        Assert.Equal(0, AccessLogRing.SlotSizeBytes % 64);
        Assert.InRange(AccessLogRing.SlotSizeBytes, 64, 512);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(1_536)]
    [InlineData(AccessLogRing.MaximumCapacity + 1)]
    public void TheCapacityMustBeAPowerOfTwoInRange(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AccessLogRing(capacity));
    }

    [Fact]
    public void AFullRingDropsAndCountsInsteadOfBlocking()
    {
        AccessLogRing ring = new(AccessLogRing.MinimumCapacity);
        for (int i = 0; i < ring.Capacity; i++)
        {
            Assert.True(ring.TryWrite(Entry(i)));
        }

        // Full: the write is refused, and the count says why.
        Assert.False(ring.TryWrite(Entry(-1)));
        Assert.False(ring.TryWrite(Entry(-2)));
        Assert.Equal(2, ring.Dropped);

        // Reading one frees exactly one slot.
        Assert.True(ring.TryRead(out _));
        Assert.True(ring.TryWrite(Entry(-3)));
        Assert.False(ring.TryWrite(Entry(-4)));
        Assert.Equal(3, ring.Dropped);

        // The drain takes the count with it, so a second report doesn't repeat the same drops.
        Assert.Equal(3, ring.TakeDropped());
        Assert.Equal(0, ring.TakeDropped());
    }

    [Fact]
    public void WrappingAroundKeepsWorking()
    {
        AccessLogRing ring = new(4);
        for (int i = 0; i < 100; i++)
        {
            Assert.True(ring.TryWrite(Entry(i)));
            Assert.True(ring.TryRead(out AccessLogEntry entry));
            Assert.Equal(Path(i), entry.Path);
        }

        Assert.Equal(0, ring.Dropped);
    }

    /// <summary>
    /// Many threads writing while one drains: no entry is torn (each is one producer's whole struct), each producer's
    /// entries keep their own order, and every drop is counted, so what was read plus what was dropped is what was offered.
    /// </summary>
    [Fact]
    public async Task ManyProducersAndOneConsumerAgreeOnWhatWasWritten()
    {
        const int Producers = 8;
        const int PerProducer = 20_000;
        AccessLogRing ring = new(4_096);
        List<AccessLogEntry> read = [];
        int finished = 0;

        CancellationToken ct = TestContext.Current.CancellationToken;
        Task consumer = Task.Run(() =>
        {
            // Drain while the producers run, then everything that is left once they have all stopped.
            while (Volatile.Read(ref finished) < Producers)
            {
                if (!ring.TryRead(out AccessLogEntry entry))
                {
                    Thread.Yield();
                    continue;
                }

                read.Add(entry);
            }

            while (ring.TryRead(out AccessLogEntry entry))
            {
                read.Add(entry);
            }
        }, ct);

        Task[] producers = new Task[Producers];
        for (int p = 0; p < Producers; p++)
        {
            int index = p;
            producers[p] = Task.Run(() =>
            {
                for (int i = 0; i < PerProducer; i++)
                {
                    ring.TryWrite(Entry((index * PerProducer) + i));
                }

                Interlocked.Increment(ref finished);
            }, ct);
        }

        await Task.WhenAll(producers).WaitAsync(ct);
        await consumer.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        long dropped = ring.Dropped;
        Assert.Equal(Producers * PerProducer, read.Count + dropped);

        // Whole entries, and each producer's own order.
        Dictionary<int, int> lastByProducer = [];
        foreach (AccessLogEntry entry in read)
        {
            (int producer, int sequence) = Parse(entry.Path!);
            if (lastByProducer.TryGetValue(producer, out int previous))
            {
                Assert.True(sequence > previous, $"producer {producer} emitted {sequence} after {previous}");
            }

            lastByProducer[producer] = sequence;
        }

        // A producer whose every write was dropped (a starved thread while the ring stayed full) contributes no line: only
        // the drop count depends on how the threads were scheduled, never the lines themselves.
        Assert.InRange(lastByProducer.Count, 1, Producers);
    }

    private static AccessLogEntry Entry(int sequence) => new() { Path = Path(sequence), StatusCode = 200 };

    private static string Path(int sequence) =>
        string.Create(CultureInfo.InvariantCulture, $"/{sequence / 20_000}/{sequence}");

    private static (int Producer, int Sequence) Parse(string path)
    {
        string[] parts = path.Split('/');
        return (int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture));
    }
}
