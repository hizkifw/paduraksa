using System.Runtime.CompilerServices;

namespace S3;

/// <summary>
/// Prototype of the M1 test helper (item 8). Runs a synchronous delegate after warm-up and reports the bytes
/// allocated on the current thread. Only meaningful for code that runs start-to-finish on one thread with no
/// awaits that actually suspend (see docs/spikes/S3-zero-alloc-gate.md, "Zero-alloc test helper").
/// </summary>
public static class AllocAssert
{
    public readonly record struct Result(long TotalBytes, int Iterations, long FirstNonZeroIteration, long MaxSingle)
    {
        public double BytesPerCall => (double)TotalBytes / Iterations;
    }

    /// <summary>
    /// Generic state instead of a capturing lambda: the delegate is static, so creating it doesn't allocate and the
    /// JIT can't hide a closure allocation inside the measured loop.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Result Measure<TState>(TState state, Action<TState> action, int warmup = 1_000, int iterations = 10_000)
    {
        // Warm-up absorbs one-off lazies (static ctors, first resize, tiering); only the measured loop below counts.
        for (var i = 0; i < warmup; i++) action(state);

        long total = 0, max = 0, first = -1;
        for (var i = 0; i < iterations; i++)
        {
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            action(state);
            var d = GC.GetAllocatedBytesForCurrentThread() - a0;
            if (d != 0)
            {
                total += d;
                if (d > max) max = d;
                if (first < 0) first = i;
            }
        }
        return new Result(total, iterations, first, max);
    }

    /// <summary>xUnit-style assertion: throws with a useful message if anything was allocated.</summary>
    public static void Zero<TState>(TState state, Action<TState> action, string what, int warmup = 1_000, int iterations = 10_000)
    {
        var r = Measure(state, action, warmup, iterations);
        if (r.TotalBytes != 0)
            throw new InvalidOperationException(
                $"{what}: allocated {r.TotalBytes} B over {r.Iterations} calls ({r.BytesPerCall:F2} B/call, max {r.MaxSingle} B in one call, first at iteration {r.FirstNonZeroIteration}).");
    }
}
