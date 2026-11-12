using System.Globalization;
using System.Runtime.CompilerServices;

namespace Paduraksa.Tests.Proxy.Testing;

/// <summary>
/// Asserts that synchronous hot-path code allocates nothing on the current thread.
/// </summary>
/// <remarks>
/// Only meaningful for code that runs start to finish on one thread: an <c>await</c> that actually suspends moves
/// the continuation to another thread and its allocations become invisible to
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/>. Pass state explicitly and use a <c>static</c> lambda so the
/// delegate itself can't hide a closure allocation inside the measured loop.
/// </remarks>
internal static class AllocAssert
{
    internal readonly record struct Result(long TotalBytes, int Iterations, int FirstNonZeroIteration, long MaxSingle, int NonZeroIterations)
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
        var nonZero = 0;
        for (var i = 0; i < iterations; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            action(state);
            var delta = GC.GetAllocatedBytesForCurrentThread() - before;
            if (delta != 0)
            {
                total += delta;
                nonZero++;
                max = Math.Max(max, delta);
                if (first < 0)
                {
                    first = i;
                }
            }
        }

        return new Result(total, iterations, first, max, nonZero);
    }

    /// <summary>
    /// <see cref="Measure"/>, re-measured once (without warm-up) when the first run saw anything; returns the second run then, with a lone
    /// allocating call in it counted as none. Use it, or <see cref="Zero"/>, wherever a result is compared with 0.
    /// </summary>
    /// <remarks>
    /// Under parallel test load the per-thread counter occasionally jumps once, by up to one allocation quantum
    /// (2.5-8 KB seen, 2 KB at iteration 6 700 of a run on CI), with nothing allocated by the code under test: always exactly one sample
    /// at a random iteration, also with tiered compilation off and with no GC during that call, never in isolation. A real hot-path allocation
    /// in deterministic code happens on every call, or at least repeatably, so it survives the re-measure and a run with more than one
    /// allocating call fails. This is the tolerance <c>GateAllocationTests</c> applies to the gate on Kestrel (<c>NonZero &lt;= 1</c>).
    /// </remarks>
    public static Result MeasureSettled<TState>(TState state, Action<TState> action, int warmup = 1_000, int iterations = 10_000)
    {
        var result = Measure(state, action, warmup, iterations);
        if (result.TotalBytes == 0)
        {
            return result;
        }

        var retry = Measure(state, action, warmup: 0, iterations);
        return retry.NonZeroIterations <= 1 ? retry with { TotalBytes = 0, MaxSingle = 0 } : retry;
    }

    /// <summary>Fails the test if the measured calls allocate (after one re-measure that forgives a lone allocating call, see <see cref="MeasureSettled"/>).</summary>
    public static void Zero<TState>(TState state, Action<TState> action, string what, int warmup = 1_000, int iterations = 10_000)
    {
        var result = Measure(state, action, warmup, iterations);
        if (result.TotalBytes == 0)
        {
            return;
        }

        var retry = Measure(state, action, warmup: 0, iterations);
        Assert.True(
            retry.NonZeroIterations <= 1,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{what}: allocated {retry.TotalBytes} B over {retry.Iterations} calls ({retry.BytesPerCall:F2} B/call, {retry.NonZeroIterations} calls allocated, max {retry.MaxSingle} B in one call, first at iteration {retry.FirstNonZeroIteration}); the first run allocated {result.TotalBytes} B."));
    }
}
