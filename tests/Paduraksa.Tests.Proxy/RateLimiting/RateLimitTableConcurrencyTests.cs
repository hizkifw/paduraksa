using System.Diagnostics;

using Paduraksa.Proxy.RateLimiting;

namespace Paduraksa.Tests.Proxy.RateLimiting;

public sealed class RateLimitTableConcurrencyTests
{
    private const ushort Slot = 1;
    private const ulong Hash = 0x0000_1234_5678;
    private const int Burst = 64;
    private const int Workers = 8;
    private const int CallsPerWorker = 4_000;

    [Fact]
    public void AThunderingHerdNeverGetsMoreThanTheBurstWhileTheClockStandsStill()
    {
        RateLimitTable table = new(RateLimitTable.MinimumCapacity);
        RateLimitParameters parameters = new(10, Burst);

        // The first request creates the bucket; every later one finds it at the start of its probe.
        Assert.True(table.TryAcquire(Slot, Hash, parameters, nowMs: 0, out _));

        var successes = Hammer(table, parameters, static () => 0);
        Assert.Equal(Burst - 1, successes);
    }

    [Fact]
    public void AnAdvancingClockNeverGivesMoreThanTheBurstPlusTheRefill()
    {
        RateLimitTable table = new(RateLimitTable.MinimumCapacity);
        RateLimitParameters parameters = new(10, Burst);
        var clock = Stopwatch.StartNew();

        Assert.True(table.TryAcquire(Slot, Hash, parameters, clock.ElapsedMilliseconds, out _));

        var successes = Hammer(table, parameters, () => clock.ElapsedMilliseconds);

        // Every take recomputes from the time it last refilled and truncates, so the total can only fall short of
        // burst + elapsed x rate; the one token of slack is the table's documented one.
        var bound = Burst + (int)(clock.ElapsedMilliseconds * parameters.TokensPerSecond / 1_000) + 1;
        Assert.InRange(successes, Burst - 1, bound);
    }

    [Fact]
    public void ContendingOnManyKeysSeesEachBucketOnlyAsOftenAsItsBurst()
    {
        const int Buckets = 64;
        RateLimitTable table = new(RateLimitTable.MinimumCapacity);
        RateLimitParameters parameters = new(10, 4);

        var successes = 0;
        Thread[] threads = new Thread[Workers];
        for (var w = 0; w < threads.Length; w++)
        {
            threads[w] = new Thread(() =>
            {
                var taken = 0;
                for (var i = 0; i < CallsPerWorker; i++)
                {
                    if (table.TryAcquire(Slot, (ulong)i & (Buckets - 1), parameters, nowMs: 0, out _))
                    {
                        taken++;
                    }
                }

                Interlocked.Add(ref successes, taken);
            })
            {
                IsBackground = true,
            };
            threads[w].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        Assert.Equal(Buckets * parameters.Burst, successes);
    }

    private static int Hammer(RateLimitTable table, in RateLimitParameters parameters, Func<long> nowMs)
    {
        // Copied out of the `in` parameter: a lambda cannot capture it.
        var rateLimits = parameters;
        var successes = 0;
        Thread[] threads = new Thread[Workers];
        for (var w = 0; w < threads.Length; w++)
        {
            threads[w] = new Thread(() =>
            {
                var taken = 0;
                for (var i = 0; i < CallsPerWorker; i++)
                {
                    if (table.TryAcquire(Slot, Hash, rateLimits, nowMs(), out _))
                    {
                        taken++;
                    }
                }

                Interlocked.Add(ref successes, taken);
            })
            {
                IsBackground = true,
            };
            threads[w].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        return successes;
    }
}
