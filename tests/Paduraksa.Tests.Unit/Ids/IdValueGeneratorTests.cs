using System.Collections.Concurrent;

using Paduraksa.Core.Ids;

namespace Paduraksa.Tests.Unit.Ids;

public sealed class IdValueGeneratorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Int64OrderedLayout()
    {
        var clock = new ManualTimeProvider(T0);
        var generator = new Int64OrderedIdGenerator(clock);
        long id = generator.Next();

        Assert.True(id > 0);
        Assert.Equal(T0, Int64OrderedIdGenerator.GetTimestamp(id));
        Assert.Equal((T0 - Int64OrderedIdGenerator.Epoch).Ticks / TimeSpan.TicksPerMillisecond, id >> Int64OrderedIdGenerator.CounterBits);
    }

    [Fact]
    public void Int64OrderedConcurrentGenerationIsUniqueAndMonotonicPerThread()
    {
        var generator = new Int64OrderedIdGenerator(TimeProvider.System);
        const int Threads = 8;
        const int PerThread = 100_000;
        var results = new ConcurrentBag<long[]>();

        Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, _ =>
        {
            long[] ids = new long[PerThread];
            for (int i = 0; i < PerThread; i++)
            {
                ids[i] = generator.Next();
            }

            results.Add(ids);
        });

        var all = new HashSet<long>();
        foreach (long[] ids in results)
        {
            for (int i = 0; i < ids.Length; i++)
            {
                Assert.True(ids[i] > 0);
                Assert.True(i == 0 || ids[i] > ids[i - 1]);
                Assert.True(all.Add(ids[i]));
            }
        }

        Assert.Equal(Threads * PerThread, all.Count);
    }

    [Fact]
    public void Int64OrderedClockGoingBackwardsStaysMonotonic()
    {
        var clock = new ManualTimeProvider(T0);
        var generator = new Int64OrderedIdGenerator(clock);

        long a = generator.Next();
        clock.Now = T0.AddHours(-1);
        long b = generator.Next();
        long c = generator.Next();
        clock.Now = T0.AddMilliseconds(1);
        long d = generator.Next();
        clock.Now = Int64OrderedIdGenerator.Epoch.AddYears(-1); // before the epoch: clamped to 0
        long e = generator.Next();

        Assert.True(a < b && b < c && c < d && d < e);
        Assert.Equal(T0, Int64OrderedIdGenerator.GetTimestamp(c)); // kept the last timestamp
        Assert.Equal(T0.AddMilliseconds(1), Int64OrderedIdGenerator.GetTimestamp(d));
    }

    [Fact]
    public void Int64OrderedCounterOverflowWithinMillisecondAdvancesLogicalClock()
    {
        var clock = new ManualTimeProvider(T0);
        var generator = new Int64OrderedIdGenerator(clock);
        int count = (1 << Int64OrderedIdGenerator.CounterBits) + 10;

        long previous = generator.Next();
        for (int i = 1; i < count; i++)
        {
            long next = generator.Next();
            Assert.True(next > previous);
            previous = next;
        }

        // More IDs than the counter can hold in one ms: the logical clock moved ahead of the (frozen) real clock.
        // One or two carries depending on the random counter seed.
        Assert.InRange(Int64OrderedIdGenerator.GetTimestamp(previous), T0.AddMilliseconds(1), T0.AddMilliseconds(2));

        // Once the real clock passes the logical clock, IDs follow real time again.
        clock.Now = T0.AddMilliseconds(5);
        Assert.Equal(T0.AddMilliseconds(5), Int64OrderedIdGenerator.GetTimestamp(generator.Next()));
    }

    [Fact]
    public void UuidV7LayoutAndMonotonicity()
    {
        var clock = new ManualTimeProvider(T0);
        var generator = new UuidV7Generator(clock);

        UInt128 previous = 0;
        for (int i = 0; i < 10_000; i++)
        {
            if (i == 5000)
            {
                clock.Now = T0.AddMinutes(-5); // clock goes backwards
            }

            Guid guid = generator.Next();
            UInt128 raw = Base62.ToUInt128(guid);
            Assert.Equal(7, guid.Version);
            Assert.Equal(0b10, (int)((raw >> 62) & 0b11)); // RFC 9562 variant
            Assert.True(raw > previous);
            previous = raw;
        }

        // 10k IDs within one frozen ms need at most ~3 borrowed milliseconds (>= 2048 per ms).
        long ms = (long)(previous >> 80);
        Assert.InRange(ms - T0.ToUnixTimeMilliseconds(), 0, 5);
    }

    [Fact]
    public void UuidV7ConcurrentGenerationIsUniqueAndMonotonicPerThread()
    {
        var generator = new UuidV7Generator(TimeProvider.System);
        const int Threads = 8;
        const int PerThread = 25_000;
        var results = new ConcurrentBag<Guid[]>();

        Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, _ =>
        {
            var ids = new Guid[PerThread];
            for (int i = 0; i < PerThread; i++)
            {
                ids[i] = generator.Next();
            }

            results.Add(ids);
        });

        var all = new HashSet<Guid>();
        foreach (Guid[] ids in results)
        {
            for (int i = 0; i < ids.Length; i++)
            {
                Assert.True(i == 0 || Base62.ToUInt128(ids[i]) > Base62.ToUInt128(ids[i - 1]));
                Assert.True(all.Add(ids[i]));
            }
        }

        Assert.Equal(Threads * PerThread, all.Count);
    }

    [Fact]
    public void UuidV4HasVersionAndVariantBits()
    {
        var seen = new HashSet<Guid>();
        for (int i = 0; i < 10_000; i++)
        {
            Guid guid = UuidV4Generator.Next();
            Assert.Equal(4, guid.Version);
            Assert.Equal(0b10, (int)((Base62.ToUInt128(guid) >> 62) & 0b11));
            Assert.True(seen.Add(guid));
        }
    }

    [Fact]
    public void GuidCompareToMatchesBigEndianAndStringOrder()
    {
        // Generated Guid IDs compare with Guid.CompareTo; it must agree with big-endian byte order and therefore
        // with the ordinal order of the base62 strings.
        Guid[] guids = [.. Enumerable.Range(0, 2000).Select(_ => UuidV4Generator.Next())];
        Assert.Equal(guids.OrderBy(Base62.ToUInt128), guids.Order());
        Assert.Equal(guids.Select(Base62.Encode).Order(StringComparer.Ordinal), guids.Order().Select(Base62.Encode));
    }

    [Fact]
    public void Int64OrderedObserveSeedsPastPersistedIds()
    {
        var clock = new ManualTimeProvider(T0);
        var generator = new Int64OrderedIdGenerator(clock);

        // Simulate a restart where the clock is behind the largest ID already in the database.
        long persisted = new Int64OrderedIdGenerator(new ManualTimeProvider(T0.AddHours(1))).Next();
        generator.Observe(persisted);
        long next = generator.Next();
        Assert.True(next > persisted);

        // Observing an older value never moves the generator backwards.
        generator.Observe(1);
        Assert.True(generator.Next() > next);

        Assert.Throws<ArgumentOutOfRangeException>(() => generator.Observe(-1));
    }

    [Fact]
    public void Int64OrderedObserveIsSafeUnderConcurrency()
    {
        var generator = new Int64OrderedIdGenerator(new ManualTimeProvider(T0));
        long floor = 1L << 60;
        var issued = new ConcurrentBag<long>();
        Parallel.For(0, 10_000, i =>
        {
            if (i % 10 == 0)
            {
                generator.Observe(floor + i);
            }
            else
            {
                issued.Add(generator.Next());
            }
        });

        Assert.Equal(issued.Count, issued.Distinct().Count());
        Assert.True(generator.Next() > floor + 9990);
    }
}
