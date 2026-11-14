using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Paduraksa.Bench.Soak;

/// <summary>
/// The ten-second sample: the server's own figures (from <c>/proc</c> and from the startup hook's file), the connections around it, the clients' interval, the publishes' and
/// the scrape's. One CSV row per sample, for the whole run; the analysis fits its leak and drift checks over them.
/// </summary>
internal sealed class Sampler(
    SoakOptions options, ServerProcess server, Recorder recorder, Publisher publisher, MetricsScraper scraper, TrafficMix mix, LongLived longLived)
{
    private static readonly string[] Columns =
    [
        "t", "pid", "rss_mb", "rss_anon_mb", "rss_file_mb", "hwm_mb", "threads", "fds", "cpu_pct", "heap_mb", "committed_mb", "retained_mb", "gen0", "gen1", "gen2", "gc_pause_ms", "tp_threads", "tp_queue",
        "lock_contention", "client_conns", "upstream_conns", "ws_open", "requests", "rps", "ok", "refused_by_design", "unexpected_5xx", "conn_errors", "timeouts", "wrong", "tolerated",
        "skipped", "small_p50_ms", "small_p99_ms", "small_p999_ms", "big_p99_ms", "ws_p99_ms", "limited_p99_ms", "admin_p99_ms", "in_flight", "publishes", "publish_mean_ms",
        "publish_max_ms", "server_requests", "server_429", "generation", "counter_violations", "probe_age_s", "scrape_ms",
    ];

    private StreamWriter? _csv;
    private long _lastTicks;
    private long _lastWall;
    private int _lastPid;

    public int Samples { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        _csv = new StreamWriter(Path.Combine(options.OutDirectory, "samples.csv")) { AutoFlush = true };
        _csv.WriteLine(string.Join(',', Columns));
        var start = Stopwatch.GetTimestamp();
        long tick = 0;
        double lastT = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                tick++;
                var due = TimeSpan.FromSeconds(tick * 10) - Stopwatch.GetElapsedTime(start);
                if (due > TimeSpan.Zero)
                {
                    await Task.Delay(due, ct);
                }

                var t = tick * 10.0;
                await SampleAsync(t, lastT, ct);
                lastT = t;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _csv.Dispose();
        }
    }

    private async Task SampleAsync(double t, double from, CancellationToken ct)
    {
        var scraped = await scraper.ScrapeAsync(ct);
        var interval = recorder.Swap();
        recorder.Seconds(interval, from, t);
        var proc = ReadProc();
        var probe = ReadProbe();
        var (clientConns, upstreamConns) = await CountConnectionsAsync(ct);
        var (publishes, publishMean, publishMax) = publisher.Since(from);
        var last = scraper.Last;
        var seconds = Math.Max(1, t - from);
        Dictionary<string, double> row = new()
        {
            ["t"] = t,
            ["pid"] = proc.Pid,
            ["rss_mb"] = proc.RssKb / 1024.0,
            ["rss_anon_mb"] = proc.AnonKb / 1024.0,
            ["rss_file_mb"] = proc.FileKb / 1024.0,
            ["hwm_mb"] = proc.HwmKb / 1024.0,
            ["threads"] = proc.Threads,
            ["fds"] = proc.Fds,
            ["cpu_pct"] = proc.CpuPct,
            ["heap_mb"] = probe.Get("heap") / 1048576.0,
            ["committed_mb"] = probe.Get("committed") / 1048576.0,
            // What survived the last collection of each generation, large and pinned object heaps included: the figure that grows when something is kept.
            ["retained_mb"] = (probe.Get("gen0_after") + probe.Get("gen1_after") + probe.Get("gen2_after") + probe.Get("gen3_after") + probe.Get("gen4_after")) / 1048576.0,
            ["gen0"] = probe.Get("gen0"),
            ["gen1"] = probe.Get("gen1"),
            ["gen2"] = probe.Get("gen2"),
            ["gc_pause_ms"] = probe.Get("pause_ms"),
            ["tp_threads"] = probe.Get("tp_threads"),
            ["tp_queue"] = probe.Get("tp_queue"),
            ["lock_contention"] = probe.Get("lock_contention"),
            ["client_conns"] = clientConns,
            ["upstream_conns"] = upstreamConns,
            ["ws_open"] = longLived.OpenSockets,
            ["requests"] = interval.Requests,
            ["rps"] = interval.Requests / seconds,
            ["ok"] = interval.Ok,
            ["refused_by_design"] = interval.Expected4xx,
            ["unexpected_5xx"] = interval.Unexpected5xx,
            ["conn_errors"] = interval.ConnectionErrors,
            ["timeouts"] = interval.Timeouts,
            ["wrong"] = interval.Wrong,
            ["tolerated"] = interval.Tolerated,
            ["skipped"] = interval.Skipped,
            ["small_p50_ms"] = interval.Small.Percentile(0.50) / 1000,
            ["small_p99_ms"] = interval.Small.Percentile(0.99) / 1000,
            ["small_p999_ms"] = interval.Small.Percentile(0.999) / 1000,
            ["big_p99_ms"] = interval.Big.Percentile(0.99) / 1000,
            ["ws_p99_ms"] = interval.Ws.Percentile(0.99) / 1000,
            ["limited_p99_ms"] = interval.Limited.Percentile(0.99) / 1000,
            ["admin_p99_ms"] = interval.Admin.Percentile(0.99) / 1000,
            ["in_flight"] = mix.InFlight,
            ["publishes"] = publishes,
            ["publish_mean_ms"] = publishMean,
            ["publish_max_ms"] = publishMax,
            ["server_requests"] = scraped ? MetricsScraper.Total(last, "paduraksa_requests_total") : double.NaN,
            ["server_429"] = scraped ? MetricsScraper.Total(last, "paduraksa_gate_outcomes_total", "outcome=\"refused_rate_limit\"") : double.NaN,
            ["generation"] = scraped ? MetricsScraper.Total(last, "paduraksa_config_generation") : double.NaN,
            ["counter_violations"] = scraper.ViolationCount,
            ["probe_age_s"] = probe.AgeSeconds,
            ["scrape_ms"] = scraper.LastScrapeMs,
        };
        _csv!.WriteLine(string.Join(',', Columns.Select(c => Format(row[c]))));
        Samples++;
    }

    private static string Format(double value) => double.IsNaN(value) ? string.Empty : value.ToString("0.###", CultureInfo.InvariantCulture);

    private (int Pid, double RssKb, double AnonKb, double FileKb, double HwmKb, int Threads, int Fds, double CpuPct) ReadProc()
    {
        var pid = server.Pid;
        if (pid == 0 || server.HasExited)
        {
            return (0, double.NaN, double.NaN, double.NaN, double.NaN, 0, 0, double.NaN);
        }

        try
        {
            double rss = 0, hwm = 0, anon = 0, file = 0;
            var threads = 0;
            foreach (var line in File.ReadLines($"/proc/{pid}/status"))
            {
                if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                {
                    rss = KbOf(line);
                }
                else if (line.StartsWith("RssAnon:", StringComparison.Ordinal))
                {
                    anon = KbOf(line);
                }
                else if (line.StartsWith("RssFile:", StringComparison.Ordinal))
                {
                    file = KbOf(line);
                }
                else if (line.StartsWith("VmHWM:", StringComparison.Ordinal))
                {
                    hwm = KbOf(line);
                }
                else if (line.StartsWith("Threads:", StringComparison.Ordinal))
                {
                    threads = int.Parse(line.AsSpan(8).Trim(), CultureInfo.InvariantCulture);
                }
            }

            var fds = Directory.EnumerateFileSystemEntries($"/proc/{pid}/fd").Count();
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            // The command is in parentheses and may hold spaces: fields are counted from the last ')'.
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            var ticks = long.Parse(fields[11], CultureInfo.InvariantCulture) + long.Parse(fields[12], CultureInfo.InvariantCulture);
            var wall = Stopwatch.GetTimestamp();
            var cpu = double.NaN;
            if (_lastPid == pid && _lastWall != 0)
            {
                var seconds = Stopwatch.GetElapsedTime(_lastWall, wall).TotalSeconds;
                cpu = (ticks - _lastTicks) / 100.0 / seconds * 100.0;
            }

            _lastPid = pid;
            _lastTicks = ticks;
            _lastWall = wall;
            return (pid, rss, anon, file, hwm, threads, fds, cpu);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (pid, double.NaN, double.NaN, double.NaN, double.NaN, 0, 0, double.NaN);
        }

        static double KbOf(string line) => double.Parse(line.Split(':')[1].Replace("kB", string.Empty).Trim(), CultureInfo.InvariantCulture);
    }

    private ProbeLine ReadProbe()
    {
        try
        {
            string? last = null;
            using FileStream stream = new(server.ProbeFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                // The hook's last key is timers: a line without it is one still being written.
                if (line.Contains("timers=", StringComparison.Ordinal))
                {
                    last = line;
                }
            }

            return ProbeLine.Parse(last);
        }
        catch (IOException)
        {
            return ProbeLine.Parse(null);
        }
    }

    private async Task<(int Client, int Upstream)> CountConnectionsAsync(CancellationToken ct)
    {
        try
        {
            ProcessStartInfo start = new("ss", ["-Htn", "state", "established"]) { RedirectStandardOutput = true, UseShellExecute = false };
            using var process = Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var client = 0;
            var upstream = 0;
            Span<Range> parts = stackalloc Range[5];
            foreach (var line in output.AsSpan().EnumerateLines())
            {
                // Columns: Recv-Q Send-Q Local:Port Peer:Port. The server's side of a client's connection is the one whose local port is a public one;
                // the proxy's side of an upstream connection is the one whose peer port is an upstream's and whose local port is not.
                if (line.Split(parts, ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) < 4)
                {
                    continue;
                }

                var local = PortOf(line[parts[2]]);
                var peer = PortOf(line[parts[3]]);
                if (local == options.PublicHttp || local == options.PublicHttps)
                {
                    client++;
                }
                else if (peer >= options.UpstreamBase && peer < options.UpstreamBase + 200 && !(local >= options.UpstreamBase && local < options.UpstreamBase + 200))
                {
                    upstream++;
                }
            }

            return (client, upstream);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return (0, 0);
        }

        static int PortOf(ReadOnlySpan<char> address) => int.TryParse(address[(address.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture, out var port) ? port : -1;
    }

    private sealed class ProbeLine
    {
        private readonly Dictionary<string, double> _values = [];

        public double AgeSeconds => _values.TryGetValue("ts", out var ts) ? (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ts) / 1000.0 : double.NaN;

        public double Get(string key) => _values.TryGetValue(key, out var value) ? value : double.NaN;

        public static ProbeLine Parse(string? line)
        {
            ProbeLine probe = new();
            foreach (var pair in (line ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0 && double.TryParse(pair[(eq + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    probe._values[pair[..eq]] = value;
                }
            }

            return probe;
        }
    }
}
