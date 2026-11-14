using System.Globalization;
using System.Text;

/// <summary>
/// The startup hook's entry point (the runtime looks for a type called <c>StartupHook</c> with this method, in the global namespace). One line per tick,
/// <c>key=value</c> pairs, appended to the file named by <c>PADURAKSA_SOAK_PROBE_FILE</c>. The sampler reads the last complete line.
/// </summary>
internal static class StartupHook
{
    private const string FileVariable = "PADURAKSA_SOAK_PROBE_FILE";
    private const string IntervalVariable = "PADURAKSA_SOAK_PROBE_SECONDS";

    public static void Initialize()
    {
        if (Environment.GetEnvironmentVariable("PADURAKSA_SOAK_HANG_FILE") is { Length: > 0 } hangFile)
        {
            HangWatch.Start(hangFile, Environment.GetEnvironmentVariable("PADURAKSA_SOAK_HANG_DUMP"));
        }

        var path = Environment.GetEnvironmentVariable(FileVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var seconds = int.TryParse(Environment.GetEnvironmentVariable(IntervalVariable), CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : 10;
        // A thread of its own: a thread-pool timer would show the very starvation it is there to report.
        Thread thread = new(() => Run(path, TimeSpan.FromSeconds(seconds))) { IsBackground = true, Name = "soak-probe" };
        thread.Start();
    }

    private static void Run(string path, TimeSpan interval)
    {
        while (true)
        {
            try
            {
                File.AppendAllText(path, Line() + "\n");
            }
#pragma warning disable CA1031 // A probe that dies must never take the server with it.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            Thread.Sleep(interval);
        }
    }

    private static string Line()
    {
        var info = GC.GetGCMemoryInfo(GCKind.Any);
        StringBuilder line = new();
        Add(line, "ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Add(line, "heap", GC.GetTotalMemory(forceFullCollection: false));
        Add(line, "committed", info.TotalCommittedBytes);
        Add(line, "fragmented", info.FragmentedBytes);
        Add(line, "gen0", GC.CollectionCount(0));
        Add(line, "gen1", GC.CollectionCount(1));
        Add(line, "gen2", GC.CollectionCount(2));
        Add(line, "pause_ms", GC.GetTotalPauseDuration().TotalMilliseconds);
        Add(line, "alloc", GC.GetTotalAllocatedBytes(precise: false));
        var generations = info.GenerationInfo;
        for (var i = 0; i < generations.Length; i++)
        {
            Add(line, "gen" + i.ToString(CultureInfo.InvariantCulture) + "_after", generations[i].SizeAfterBytes);
        }

        Add(line, "tp_threads", ThreadPool.ThreadCount);
        Add(line, "tp_queue", ThreadPool.PendingWorkItemCount);
        Add(line, "tp_completed", ThreadPool.CompletedWorkItemCount);
        Add(line, "lock_contention", Monitor.LockContentionCount);
        Add(line, "timers", Timer.ActiveCount);
        // The proxy's workaround for Kestrel's lost HTTP/2 responses (KestrelHttp2StreamReuse): how many requests it put right.
        if (Type.GetType("Paduraksa.Proxy.Http.KestrelHttp2StreamReuse, Paduraksa.Proxy")?.GetProperty("Healed")?.GetValue(null) is long healed)
        {
            Add(line, "h2_healed", healed);
        }

        return line.ToString().TrimEnd();
    }

    private static void Add(StringBuilder line, string key, double value) =>
        line.Append(key).Append('=').Append(value.ToString("0.###", CultureInfo.InvariantCulture)).Append(' ');

    private static void Add(StringBuilder line, string key, long value) =>
        line.Append(key).Append('=').Append(value.ToString(CultureInfo.InvariantCulture)).Append(' ');
}
