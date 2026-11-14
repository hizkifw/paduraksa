using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Paduraksa.Bench.Soak;

internal sealed record CounterViolation(double T, string Series, double Previous, double Now, bool AfterRestart);

/// <summary>
/// Reads <c>/metrics</c> every sample and checks the continuity the S6 exit criterion names: every counter and histogram series only goes up, between one scrape and the next,
/// whatever was published in between. The one allowed drop is the scrape after a restart (the process's counters are its own; the persisted rollups are what carries on).
/// </summary>
internal sealed class MetricsScraper(AdminClient admin, Events events, Func<int> serverPid)
{
    private readonly List<CounterViolation> _violations = [];
    private readonly Dictionary<string, string> _types = new(StringComparer.Ordinal);
    private Dictionary<string, double> _previous = [];
    private int _lastPid;

    public IReadOnlyList<CounterViolation> Violations => _violations;

    public long ViolationCount { get; private set; }

    public long Scrapes { get; private set; }

    public long Failures { get; private set; }

    public double LastScrapeMs { get; private set; }

    public Dictionary<string, double> Last { get; private set; } = [];

    public (double Before, double After)? RestartTotals { get; private set; }

    public async Task<bool> ScrapeAsync(CancellationToken ct)
    {
        AdminResult result;
        try
        {
            result = await admin.GetAsync("/metrics", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Failures++;
            return false;
        }

        if (!result.Ok)
        {
            Failures++;
            return false;
        }

        LastScrapeMs = result.Milliseconds;
        Scrapes++;
        var current = Parse(result.Body);
        var t = events.ElapsedSeconds;
        // A new process starts its counters again: the one comparison that must not be made.
        var pid = serverPid();
        if (_lastPid != 0 && pid != _lastPid)
        {
            RestartTotals = (Total(_previous, "paduraksa_requests_total"), Total(current, "paduraksa_requests_total"));
            events.Write("restart.counters", new { before = RestartTotals.Value.Before, after = RestartTotals.Value.After });
        }
        else
        {
            foreach (var (key, now) in current)
            {
                if (_previous.TryGetValue(key, out var before) && now < before - 1e-9 && IsMonotonic(key))
                {
                    ViolationCount++;
                    if (_violations.Count < 200)
                    {
                        _violations.Add(new CounterViolation(Math.Round(t, 1), key, before, now, false));
                    }
                }
            }
        }

        _lastPid = pid;
        _previous = current;
        Last = current;
        return true;
    }

    public static double Total(Dictionary<string, double> metrics, string name, string? labelFragment = null)
    {
        var total = 0.0;
        foreach (var (key, value) in metrics)
        {
            if (key.StartsWith(name + "{", StringComparison.Ordinal) && (labelFragment is null || key.Contains(labelFragment, StringComparison.Ordinal)))
            {
                total += value;
            }
        }

        return total;
    }

    private bool IsMonotonic(string key)
    {
        var name = key.AsSpan(0, key.IndexOf('{'));
        foreach (var suffix in (ReadOnlySpan<string>)["_bucket", "_sum", "_count"])
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        return _types.TryGetValue(name.ToString(), out var type) && type is "counter" or "histogram";
    }

    private Dictionary<string, double> Parse(string text)
    {
        Dictionary<string, double> values = new(StringComparer.Ordinal);
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '#')
            {
                if (line.StartsWith("# TYPE ", StringComparison.Ordinal))
                {
                    var parts = line[7..].ToString().Split(' ');
                    if (parts.Length == 2)
                    {
                        _types[parts[0]] = parts[1];
                    }
                }

                continue;
            }

            var space = line.LastIndexOf(' ');
            if (space > 0 && double.TryParse(line[(space + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                var key = line[..space].ToString();
                values[key.Contains('{', StringComparison.Ordinal) ? key : key + "{}"] = value;
            }
        }

        return values;
    }
}

/// <summary>What the upstream health board said, once a second: the flapped destination's verdict over time, and any verdict against a destination that is up.</summary>
internal sealed class HealthWatcher(AdminClient admin, SoakConfig config, Events events, Recorder recorder)
{
    private readonly List<(double T, string State, string? Reason)> _flapTimeline = [];
    private readonly List<object> _unexpected = [];
    private readonly Dictionary<string, string> _upstreamOf = config.Upstreams
        .SelectMany(plan => plan.Nodes.Concat(plan.Spare is null ? [] : [plan.Spare]).Select(node => (node.Port, plan.Name)))
        .ToDictionary(x => ":" + x.Port.ToString(CultureInfo.InvariantCulture), x => x.Name);

    public long Polls { get; private set; }

    public long Failures { get; private set; }

    public IReadOnlyList<(double T, string State, string? Reason)> FlapTimeline => _flapTimeline;

    public IReadOnlyList<object> Unexpected => _unexpected;

    public long DownVerdictsOnLiveDestinations { get; private set; }

    public long FlappingFlags { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(500));
        string? lastFlapState = null;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                AdminResult result;
                try
                {
                    result = await admin.GetAsync("/api/v1/upstreams/health", ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    Failures++;
                    continue;
                }

                if (!result.Ok || result.Json is not JsonArray upstreams)
                {
                    Failures++;
                    continue;
                }

                Polls++;
                var t = events.ElapsedSeconds;
                var inRestart = recorder.Tolerance.Applies(t, "admin");
                var flapPort = ":" + config.FlapNode.Port.ToString(CultureInfo.InvariantCulture);
                foreach (var upstream in upstreams)
                {
                    foreach (var destination in upstream!["destinations"]!.AsArray())
                    {
                        var address = destination!["address"]!.GetValue<string>();
                        var state = destination["state"]!.GetValue<string>();
                        var reason = destination["reason"]?.GetValue<string>();
                        if (address.EndsWith(flapPort, StringComparison.Ordinal))
                        {
                            if (state != lastFlapState)
                            {
                                _flapTimeline.Add((Math.Round(t, 2), state, reason));
                                lastFlapState = state;
                            }

                            continue;
                        }

                        if (destination["flapping"]?.GetValue<bool>() == true)
                        {
                            FlappingFlags++;
                        }

                        // A destination that is up and answering must never be reported down; "unknown" (not yet probed) is not a verdict.
                        if (state is not ("healthy" or "unknown") && !inRestart)
                        {
                            DownVerdictsOnLiveDestinations++;
                            if (_unexpected.Count < 100)
                            {
                                _unexpected.Add(new { t = Math.Round(t, 1), address, state, reason, upstream = _upstreamOf.GetValueOrDefault(address[address.LastIndexOf(':')..]) });
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}

/// <summary>The admin side's own traffic: what a person with the dashboard and a script would poll, at their rates. A failure outside the restart is an error.</summary>
internal sealed class AdminPoller(AdminClient admin, Recorder recorder, Events events)
{
    private static readonly (string Path, int EverySeconds)[] Endpoints =
    [
        ("/api/v1/metrics/overview?range=hour", 3),
        ("/api/v1/analytics?range=hour", 15),
        ("/api/v1/metrics/overview?range=day", 30),
        ("/api/v1/analytics?range=day", 45),
        ("/api/v1/revisions?limit=10", 20),
        ("/api/v1/audit?limit=20", 30),
        ("/api/v1/hosts?limit=50", 25),
        ("/api/v1/security/overview", 30),
        ("/api/v1/logs/recent?limit=100", 10),
        ("/api/v1/version", 60),
    ];

    private long _calls;
    private long _errors;

    public long Calls => Interlocked.Read(ref _calls);

    public long Errors => Interlocked.Read(ref _errors);

    public Task RunAsync(CancellationToken ct) => Task.WhenAll(Endpoints.Select(e => Task.Run(() => LoopAsync(e.Path, e.EverySeconds, ct), ct)));

    private async Task LoopAsync(string path, int everySeconds, CancellationToken ct)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(everySeconds));
        try
        {
            do
            {
                try
                {
                    Interlocked.Increment(ref _calls);
                    var result = await admin.GetAsync(path, ct);
                    recorder.Admin(result.Milliseconds * 1000);
                    if (!result.Ok)
                    {
                        Fail(path, "HTTP " + (int)result.Status);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    Fail(path, Recorder.KindOf(ex));
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Fail(string path, string why)
    {
        Interlocked.Increment(ref _errors);
        recorder.Fail("admin-error", "admin", path, "admin", why);
        _ = events;
    }
}

/// <summary>An MCP client: a call a minute, alternating <c>search</c> (the OpenAPI spec) and <c>execute</c> (a read through the sandbox). Each answer has to be a success.</summary>
internal sealed class McpLoop(SoakOptions options, AdminClient admin, Recorder recorder, Events events)
{
    private readonly List<object> _calls = [];
    private int _id;

    public IReadOnlyList<object> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public long Failures { get; private set; }

    public async Task RunAsync(int everySeconds, CancellationToken ct)
    {
        using HttpClient http = new(new HttpClientHandler { ServerCertificateCustomValidationCallback = static (_, _, _, _) => true }) { Timeout = TimeSpan.FromSeconds(60) };
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(everySeconds));
        var n = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var search = n++ % 2 == 0;
                var code = search
                    ? "async () => Object.keys(spec.paths).filter(p => p.includes('hosts')).length"
                    : "async () => { const r = await paduraksa.request({ method: 'GET', path: '/api/v1/hosts', query: { limit: 5 } }); return { status: r.status, items: r.body.items.length }; }";
                await CallAsync(http, search ? "search" : "execute", code, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task CallAsync(HttpClient http, string tool, string code, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        string? problem = null;
        try
        {
            var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = Interlocked.Increment(ref _id), method = "tools/call", @params = new { name = tool, arguments = new { code } } });
            using HttpRequestMessage request = new(HttpMethod.Post, $"https://127.0.0.1:{options.Admin}/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + admin.Token);
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                problem = "HTTP " + (int)response.StatusCode;
            }
            else
            {
                var data = text.Split('\n').FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal));
                var result = data is null ? null : JsonNode.Parse(data[5..])?["result"];
                if (result is null || result["isError"]?.GetValue<bool>() != false)
                {
                    problem = "the tool answered an error: " + (text.Length > 200 ? text[..200] : text);
                }
                else if (tool == "execute" && !(result["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty).Contains("\"status\":200", StringComparison.Ordinal))
                {
                    problem = "execute did not return the read's status 200";
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            problem = Recorder.KindOf(ex);
        }

        var t = events.ElapsedSeconds;
        var tolerated = recorder.Tolerance.Applies(t, "admin");
        lock (_calls)
        {
            _calls.Add(new { t = Math.Round(t, 1), tool, ms = Math.Round(watch.Elapsed.TotalMilliseconds), ok = problem is null, problem, tolerated });
        }

        if (problem is not null && !tolerated)
        {
            Failures++;
            recorder.Fail("mcp-error", "admin", "/mcp", "mcp", tool + ": " + problem);
        }
    }
}
