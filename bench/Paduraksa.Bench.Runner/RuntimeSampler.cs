using System.Globalization;

namespace Paduraksa.Bench.Runner;

/// <summary>
/// Prints one line per second of process-wide runtime counters, for bench/baseline/summarize.py to line up with
/// each oha run's time window:
/// <code>
/// METRICS ts=&lt;unix ms&gt; gen0=&lt;n&gt; gen1=&lt;n&gt; gen2=&lt;n&gt; pause_ms=&lt;total&gt; max_pause_ms=&lt;x&gt; alloc=&lt;bytes&gt; tp_queue=&lt;n&gt; tp_threads=&lt;n&gt;
/// </code>
/// <list type="bullet">
/// <item><c>gen0/1/2</c>: <see cref="GC.CollectionCount"/> (cumulative; gen0 includes gen1 and gen2 GCs).</item>
/// <item><c>pause_ms</c>: <see cref="GC.GetTotalPauseDuration"/> (cumulative).</item>
/// <item><c>max_pause_ms</c>: the longest single pause among GCs that finished since the previous line. The runtime
/// only keeps the latest GC of each kind (ephemeral, full blocking, background), so when several GCs of one kind
/// fit in a second, only the last is seen; the cumulative <c>pause_ms</c> still counts them all.</item>
/// <item><c>alloc</c>: <see cref="GC.GetTotalAllocatedBytes(bool)"/> with <c>precise: true</c> (cumulative). It can go
/// backwards under Server GC (spike S3); the consumer drops such samples.</item>
/// <item><c>tp_queue</c>/<c>tp_threads</c>: <see cref="ThreadPool.PendingWorkItemCount"/> and
/// <see cref="ThreadPool.ThreadCount"/> at the sample.</item>
/// </list>
/// </summary>
internal static class RuntimeSampler
{
    private static readonly GCKind[] Kinds = [GCKind.Ephemeral, GCKind.FullBlocking, GCKind.Background];

    public static async Task RunAsync(TextWriter output, CancellationToken cancellationToken)
    {
        var lastIndex = new long[Kinds.Length];
        for (var k = 0; k < Kinds.Length; k++)
        {
            lastIndex[k] = GC.GetGCMemoryInfo(Kinds[k]).Index;
        }

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var maxPause = TimeSpan.Zero;
                for (var k = 0; k < Kinds.Length; k++)
                {
                    var info = GC.GetGCMemoryInfo(Kinds[k]);
                    if (info.Index == lastIndex[k])
                    {
                        continue;
                    }

                    lastIndex[k] = info.Index;
                    foreach (var pause in info.PauseDurations)
                    {
                        if (pause > maxPause)
                        {
                            maxPause = pause;
                        }
                    }
                }

                var line = string.Create(
                    CultureInfo.InvariantCulture,
                    $"METRICS ts={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)} pause_ms={GC.GetTotalPauseDuration().TotalMilliseconds:F3} max_pause_ms={maxPause.TotalMilliseconds:F3} alloc={GC.GetTotalAllocatedBytes(precise: true)} tp_queue={ThreadPool.PendingWorkItemCount} tp_threads={ThreadPool.ThreadCount}");
                await output.WriteLineAsync(line).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }
}
