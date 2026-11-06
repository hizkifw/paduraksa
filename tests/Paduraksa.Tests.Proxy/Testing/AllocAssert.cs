using System.Globalization;
using System.Runtime.CompilerServices;

namespace Paduraksa.Tests.Proxy.Testing;

/// <summary>
/// Asserts that synchronous hot-path code allocates nothing on the current thread (PLAN §7.1, spike S3).
/// </summary>
/// <remarks>
/// Only meaningful for code that runs start to finish on one thread: an <c>await</c> that actually suspends moves
/// the continuation to another thread and its allocations become invisible to
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/>. Pass state explicitly and use a <c>static</c> lambda so the
/// delegate itself can't hide a closure allocation inside the measured loop.
/// </remarks>
internal static class AllocAssert
{
    internal readonly record struct Result(long TotalBytes, int Iterations, int FirstNonZeroIteration, long MaxSingle)
    {
        public double BytesPerCall => (double)TotalBytes / Iterations;
    }

    /// <summary>Runs <paramref name="action"/> <paramref name="warmup"/> times, then measures each of
    /// <paramref name="iterations"/> calls individually.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Result Measure<TState>(TState state, Action<TState> action, int warmup = 1_000, int iterations = 10_000)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfNegative(warmup);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);

        // Warm-up absorbs one-off costs (static constructors, first resizes, tiered JIT); only the loop below counts.
        for (var i = 0; i < warmup; i++)
        {
            action(state);
        }

        long total = 0;
        long max = 0;
        var first = -1;
        for (var i = 0; i < iterations; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            action(state);
            var delta = GC.GetAllocatedBytesForCurrentThread() - before;
            if (delta != 0)
            {
                total += delta;
                max = Math.Max(max, delta);
                if (first < 0)
                {
                    first = i;
                }
            }
        }

        return new Result(total, iterations, first, max);
    }

    /// <summary>Fails the test if any measured call allocated.</summary>
    public static void Zero<TState>(TState state, Action<TState> action, string what, int warmup = 1_000, int iterations = 10_000)
    {
        var result = Measure(state, action, warmup, iterations);
        Assert.True(
            result.TotalBytes == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{what}: allocated {result.TotalBytes} B over {result.Iterations} calls ({result.BytesPerCall:F2} B/call, max {result.MaxSingle} B in one call, first at iteration {result.FirstNonZeroIteration})."));
    }
}
