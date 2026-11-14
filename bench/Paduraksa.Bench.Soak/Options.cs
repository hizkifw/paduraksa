using System.Globalization;

namespace Paduraksa.Bench.Soak;

/// <summary>Everything the script passes in, as <c>--name value</c> pairs, and the ports and CPU sets the run uses.</summary>
internal sealed class SoakOptions
{
    public required string OutDirectory { get; init; }

    public required string ServerDll { get; init; }

    public required string ProbeDll { get; init; }

    public int DurationSeconds { get; init; } = 3600;

    public int Rate { get; init; } = 4000;

    public string ServerCpus { get; init; } = "0-3";

    public string? ChromiumPath { get; init; }

    public bool Browser { get; init; } = true;

    public bool Mcp { get; init; } = true;

    public bool Http3 { get; init; }

    public int WebSockets { get; init; } = 300;

    public int PublicHttp { get; init; } = 18081;

    public int PublicHttps { get; init; } = 18443;

    public int Admin { get; init; } = 18943;

    public int UpstreamBase { get; init; } = 19000;

    public int AuthPort { get; init; } = 19900;

    public int Seed { get; init; } = 20261114;

    /// <summary>Set the server up, print the API token and wait: for trying the config by hand.</summary>
    public bool Hold { get; init; }

    /// <summary>Keep the server's log directory (the access log of every request) in the run's output: for chasing one request, and far too big for a full run.</summary>
    public bool KeepLogs { get; init; }

    public static SoakOptions Parse(string[] args)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{args[i]}'.");
            }

            values[args[i][2..]] = args[i + 1];
        }

        string Get(string key, string fallback) => values.TryGetValue(key, out var value) ? value : fallback;
        int GetInt(string key, int fallback) => int.Parse(Get(key, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
        bool GetBool(string key, bool fallback) => Get(key, fallback ? "true" : "false") is "true" or "1";

        return new SoakOptions
        {
            OutDirectory = Path.GetFullPath(values.TryGetValue("out", out var o) ? o : throw new ArgumentException("--out is required")),
            ServerDll = Path.GetFullPath(values.TryGetValue("server-dll", out var s) ? s : throw new ArgumentException("--server-dll is required")),
            ProbeDll = Path.GetFullPath(values.TryGetValue("probe-dll", out var p) ? p : throw new ArgumentException("--probe-dll is required")),
            DurationSeconds = GetInt("duration", 3600),
            Rate = GetInt("rate", 4000),
            ServerCpus = Get("server-cpus", "0-3"),
            ChromiumPath = values.TryGetValue("chromium", out var c) && c.Length > 0 ? c : null,
            Browser = GetBool("browser", true),
            Mcp = GetBool("mcp", true),
            Http3 = GetBool("http3", false),
            WebSockets = GetInt("websockets", 300),
            PublicHttp = GetInt("http", 18081),
            PublicHttps = GetInt("https", 18443),
            Admin = GetInt("admin", 18943),
            UpstreamBase = GetInt("upstream-base", 19000),
            AuthPort = GetInt("auth-port", 19900),
            Seed = GetInt("seed", 20261114),
            Hold = GetBool("hold", false),
            KeepLogs = GetBool("keep-logs", false),
        };
    }
}

/// <summary>
/// When things happen, in seconds from the start of the load, scaled to the run's length so <c>--quick</c> exercises the same events in a few minutes.
/// The analysis reads the same numbers from <c>plan.json</c>.
/// </summary>
internal sealed record SoakPlan(
    int DurationSeconds,
    int WarmupSeconds,
    int FlapStart,
    int FlapSeconds,
    int BurstStart,
    int BurstSeconds,
    int RestartAt,
    int BaselineStart,
    int BaselineEnd,
    int FinalStart,
    int FinalEnd)
{
    /// <summary>How long after the restart's stop signal errors are expected: the downtime, the clients' reconnects and the pools' first cold requests.</summary>
    public const int RestartGraceSeconds = 15;

    public static SoakPlan For(int duration)
    {
        var warmup = Math.Min(300, (int)(duration * 0.2));
        var window = Math.Min(600, (int)(duration * 0.17));
        var flap = Math.Clamp((int)(duration * 0.1), 30, 60);
        var burst = Math.Clamp((int)(duration * 0.1), 30, 60);
        var flapStart = duration / 3;
        return new SoakPlan(duration, warmup, flapStart, flap, flapStart + 5, burst, duration / 2, warmup, warmup + window, duration - window, duration);
    }
}
