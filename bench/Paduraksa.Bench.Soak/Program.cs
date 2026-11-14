using System.Diagnostics;
using System.Globalization;
using System.Net.Quic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

using Paduraksa.Bench.Soak;

// The soak test's driver. bench/soak/run-soak.sh builds it and runs it; bench/soak/README.md says what it does and why.
var options = SoakOptions.Parse(args);
Directory.CreateDirectory(options.OutDirectory);
var plan = SoakPlan.For(options.DurationSeconds);
using Events events = new(Path.Combine(options.OutDirectory, "events.jsonl"));
var config = SoakConfig.Build(options);
var dataDirectory = Path.Combine(Path.GetTempPath(), "paduraksa-soak-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
Directory.CreateDirectory(dataDirectory);
ServerProcess server = new(options, dataDirectory, Path.Combine(options.OutDirectory, "server.log"));
AuthProvider authProvider = new(options.AuthPort);
using CancellationTokenSource runCts = new();
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; runCts.Cancel(); });
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; runCts.Cancel(); });

var exitCode = 0;
var indented = new JsonSerializerOptions { WriteIndented = true };
try
{
    exitCode = await RunAsync();
}
catch (Exception ex)
{
    events.Write("fatal", new { error = ex.ToString() });
    Console.Error.WriteLine(ex);
    exitCode = 3;
}
finally
{
    server.KillHard();
    await authProvider.DisposeAsync();
    foreach (var node in config.AllNodes)
    {
        await node.DisposeAsync();
    }

    try
    {
        Directory.Delete(dataDirectory, recursive: true);
    }
    catch (IOException)
    {
    }
}

return exitCode;

async Task<int> RunAsync()
{
    var ct = runCts.Token;
    foreach (var node in config.AllNodes)
    {
        await node.StartAsync();
    }

    await authProvider.StartAsync();
    server.Start();
    // So the script can stop the server if this process is killed before it can.
    await File.WriteAllTextAsync(Path.Combine(options.OutDirectory, "server.pid"), server.Pid.ToString(CultureInfo.InvariantCulture), ct);
    Control control = new(options, server, config, events);
    await control.WaitReadyAsync(TimeSpan.FromSeconds(90), ct);
    await control.SetUpAsync(ct);
    await control.ImportConfigAsync(ct);
    await WaitForHealthyAsync(control.Admin, ct);

    if (options.Hold)
    {
        Console.WriteLine("HOLD token=" + control.Token);
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }

        await server.StopAsync();
        return 0;
    }

    if (Environment.GetEnvironmentVariable("PADURAKSA_SOAK_DEBUG_TOKEN") is { Length: > 0 } tokenFile)
    {
        // For poking at a run by hand; never set by the script.
        await File.WriteAllTextAsync(tokenFile, control.Token, ct);
    }

    ResourceStore store = new(control.Admin);
    await store.LoadAsync("hosts", "names", ct);
    await store.LoadAsync("upstreams", "name", ct);
    await store.LoadAsync("rate-limit-policies", "name", ct);
    await store.LoadAsync("access-lists", "name", ct);

    Recorder recorder = new(events, plan);
    using TrafficClients clients = new(options);
    TrafficMix mix = new(options, config, clients, recorder, events);
    Publisher publisher = new(options, config, store, events, recorder);
    MetricsScraper scraper = new(control.Admin, events, () => server.Pid);
    HealthWatcher health = new(control.Admin, config, events, recorder);
    AdminPoller poller = new(control.Admin, recorder, events);
    McpLoop mcp = new(options, control.Admin, recorder, events);
    LongLived longLived = new(options, recorder, events);
    await using BrowserTabs browser = new(options, recorder, events);
    Sampler sampler = new(options, server, recorder, publisher, scraper, mix, longLived);
    Facts facts = new();

    var browserStarted = options.Browser && await browser.StartAsync(ct);
    await File.WriteAllTextAsync(
        Path.Combine(options.OutDirectory, "plan.json"),
        JsonSerializer.Serialize(new
        {
            plan,
            options.Rate,
            options.WebSockets,
            http3 = clients.HasHttp3,
            http3Requested = options.Http3,
            quicSupported = QuicConnection.IsSupported,
            browser = browserStarted ? "chromium" : options.Browser ? browser.Mode : "off",
            mcp = options.Mcp,
            hosts = config.AllHostNames.Count,
            upstreams = config.Upstreams.Count,
            destinations = config.AllNodes.Count,
            serverCpus = options.ServerCpus,
            flapNode = config.FlapNode.Name,
            flapHosts = config.FlapNames,
        }, indented),
        ct);

    // The load's zero. Everything in the plan is measured from here, and the analysis lines events up with samples through it.
    events.ResetClock();
    events.Write("load.begin", new { plan.DurationSeconds, options.Rate });
    using CancellationTokenSource loadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    var load = loadCts.Token;
    // For finding what a symptom depends on: PADURAKSA_SOAK_SKIP=mix,longlived,... leaves those parts of the load out. Never set by the script.
    var skip = (Environment.GetEnvironmentVariable("PADURAKSA_SOAK_SKIP") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
    List<Task> tasks = [];
    void Part(string name, Func<Task> run)
    {
        if (!skip.Contains(name))
        {
            tasks.Add(Task.Run(run, CancellationToken.None));
        }
    }

    Part("mix", () => mix.RunAsync(load));
    Part("limited", () => mix.RunLimitedAsync(100, load));
    Part("pow", () => mix.RunPowAsync(Math.Max(5, options.Rate / 150), load));
    Part("longlived", () => longLived.RunAsync(load));
    Part("publisher", () => publisher.RunCycleAsync(load));
    Part("health", () => health.RunAsync(load));
    Part("admin", () => poller.RunAsync(load));
    Part("browser", () => browser.RunAsync(load));
    Part("schedule", () => ScheduleAsync(load));
    if (options.Mcp)
    {
        tasks.Add(Task.Run(() => mcp.RunAsync(60, load), CancellationToken.None));
    }

    using CancellationTokenSource samplerCts = new();
    var samplerTask = Task.Run(() => sampler.RunAsync(samplerCts.Token), CancellationToken.None);

    try
    {
        await Task.Delay(TimeSpan.FromSeconds(plan.DurationSeconds), ct);
    }
    catch (OperationCanceledException)
    {
        events.Write("run.interrupted");
    }

    events.Write("load.end");
    browser.StopWatching();
    await loadCts.CancelAsync();
    await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(TimeSpan.FromSeconds(30)));
    // One more sample after the load stops, so the last interval is in the file.
    await Task.Delay(TimeSpan.FromSeconds(11), CancellationToken.None);
    await samplerCts.CancelAsync();
    await samplerTask;

    facts.Final = await Snapshot(control.Admin);
    var stop = await server.StopAsync();
    events.Write("server.stopped", new { exitCode = stop.ExitCode, seconds = Math.Round(stop.Took.TotalSeconds, 2) });
    facts.FinalStop = new JsonObject { ["exitCode"] = stop.ExitCode, ["seconds"] = Math.Round(stop.Took.TotalSeconds, 2) };
    await WriteResultsAsync();
    return 0;

    async Task ScheduleAsync(CancellationToken token)
    {
        try
        {
            await Task.WhenAll(FlapAsync(token), BurstAsync(token), RestartAsync(token));
        }
        catch (OperationCanceledException)
        {
        }
    }

    async Task DelayUntilAsync(int seconds, CancellationToken token)
    {
        var wait = seconds - events.ElapsedSeconds;
        if (wait > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(wait), token);
        }
    }

    async Task BurstAsync(CancellationToken token)
    {
        await DelayUntilAsync(plan.BurstStart, token);
        await publisher.RunBurstAsync(plan.BurstSeconds, token);
    }

    async Task FlapAsync(CancellationToken token)
    {
        await DelayUntilAsync(plan.FlapStart, token);
        var stopT = events.ElapsedSeconds;
        recorder.Tolerance.Flap(plan.FlapStart, plan.FlapStart + plan.FlapSeconds + 30, config.FlapNames);
        events.Write("flap.stop", new { node = config.FlapNode.Name, port = config.FlapNode.Port });
        var requestsAtStop = config.FlapNode.Requests;
        await config.FlapNode.StopAsync();
        await DelayUntilAsync(plan.FlapStart + plan.FlapSeconds, token);
        var startT = events.ElapsedSeconds;
        await config.FlapNode.StartAsync();
        events.Write("flap.start", new { node = config.FlapNode.Name });
        var requestsAtStart = config.FlapNode.Requests;
        await DelayUntilAsync(plan.FlapStart + plan.FlapSeconds + 45, token);
        var history = await control.Admin.GetAsync("/api/v1/notifications/history?limit=100", token);
        facts.Flap = new JsonObject
        {
            ["stopT"] = Math.Round(stopT, 2),
            ["startT"] = Math.Round(startT, 2),
            ["requestsBeforeStop"] = requestsAtStop,
            ["requestsAfterRestart"] = config.FlapNode.Requests - requestsAtStart,
            ["history"] = history.Ok ? history.Json : history.Body,
        };
        events.Write("flap.checked", new { afterRestartRequests = config.FlapNode.Requests - requestsAtStart });
    }

    async Task RestartAsync(CancellationToken token)
    {
        await DelayUntilAsync(plan.RestartAt, token);
        var begin = events.ElapsedSeconds;
        var beginWall = DateTimeOffset.UtcNow;
        var pre = await Snapshot(control.Admin);
        recorder.Tolerance.Restart((int)begin, int.MaxValue);
        events.Write("restart.begin", new { pid = server.Pid });
        var (exitCode, took) = await server.StopAsync();
        events.Write("restart.stopped", new { exitCode, seconds = Math.Round(took.TotalSeconds, 2) });
        server.Start();
        await control.WaitReadyAsync(TimeSpan.FromSeconds(120), token);
        var readyT = events.ElapsedSeconds;
        events.Write("restart.ready", new { secondsFromSignal = Math.Round(readyT - begin, 2) });
        var proxied = await WaitProxiedAsync(token);
        var proxiedT = events.ElapsedSeconds;
        var healthy = await WaitForHealthyAsync(control.Admin, token, quiet: true);
        var healthyT = events.ElapsedSeconds;
        recorder.Tolerance.Restart((int)begin, (int)Math.Ceiling(Math.Max(readyT, Math.Max(proxiedT, healthyT)) + SoakPlan.RestartGraceSeconds));
        await browser.ReloadAsync();
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(75, Math.Max(35, plan.DurationSeconds / 20.0))), token);
        var post = await Snapshot(control.Admin);
        facts.Restart = new JsonObject
        {
            ["beginT"] = Math.Round(begin, 2),
            ["beginWall"] = beginWall.ToString("O", CultureInfo.InvariantCulture),
            ["stopExitCode"] = exitCode,
            ["stopSeconds"] = Math.Round(took.TotalSeconds, 2),
            ["readySecondsFromSignal"] = Math.Round(readyT - begin, 2),
            ["firstProxiedOkSecondsFromSignal"] = proxied ? Math.Round(proxiedT - begin, 2) : null,
            ["upstreamsHealthySecondsFromSignal"] = healthy ? Math.Round(healthyT - begin, 2) : null,
            ["counters"] = scraper.RestartTotals is { } totals ? new JsonObject { ["before"] = totals.Before, ["after"] = totals.After } : null,
            ["pre"] = pre,
            ["post"] = post,
        };
    }

    async Task<bool> WaitProxiedAsync(CancellationToken token)
    {
        using HttpClient http = new(new SocketsHttpHandler { ConnectCallback = async (context, c) => await Loopback.ConnectAsync(context.DnsEndPoint.Port, c) }) { Timeout = TimeSpan.FromSeconds(3) };
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(60))
        {
            try
            {
                using var response = await http.GetAsync($"http://{SoakConfig.Name("app01")}:{options.PublicHttp}/p/1", token);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!token.IsCancellationRequested)
            {
            }

            await Task.Delay(100, token);
        }

        return false;
    }

    async Task WriteResultsAsync()
    {
        var (okPerSecond, refusedPerSecond) = recorder.LimitedPerSecond;
        var (idle, slowloris, slowBody) = longLived.Cuts;
        JsonObject root = new()
        {
            ["requests"] = recorder.Summary(),
            ["errorKinds"] = JsonSerializer.SerializeToNode(recorder.ErrorKinds),
            ["errors"] = JsonSerializer.SerializeToNode(recorder.Errors),
            ["latency"] = new JsonObject
            {
                ["baseline"] = Window(recorder.Baseline),
                ["final"] = Window(recorder.Final),
                ["baselineBig"] = Window(recorder.BaselineBig),
                ["finalBig"] = Window(recorder.FinalBig),
            },
            ["limited"] = new JsonObject
            {
                ["ok"] = JsonSerializer.SerializeToNode(okPerSecond.Take(plan.DurationSeconds + 1)),
                ["refused"] = JsonSerializer.SerializeToNode(refusedPerSecond.Take(plan.DurationSeconds + 1)),
                ["perSecondLimit"] = SoakConfig.LimitedPerSecond,
            },
            ["canary"] = new JsonObject { ["highestSeen"] = mix.CanaryHighest, ["published"] = publisher.Revision },
            ["counters"] = new JsonObject
            {
                ["scrapes"] = scraper.Scrapes,
                ["scrapeFailures"] = scraper.Failures,
                ["violations"] = scraper.ViolationCount,
                ["violationList"] = JsonSerializer.SerializeToNode(scraper.Violations),
            },
            ["health"] = new JsonObject
            {
                ["polls"] = health.Polls,
                ["pollFailures"] = health.Failures,
                ["flapNodeTimeline"] = JsonSerializer.SerializeToNode(health.FlapTimeline.Select(x => new { x.T, x.State, x.Reason })),
                ["downVerdictsOnLiveDestinations"] = health.DownVerdictsOnLiveDestinations,
                ["flappingFlags"] = health.FlappingFlags,
                ["unexpected"] = JsonSerializer.SerializeToNode(health.Unexpected),
            },
            ["flap"] = facts.Flap,
            ["restart"] = facts.Restart,
            ["final"] = facts.Final,
            ["finalStop"] = facts.FinalStop,
            ["webSockets"] = new JsonObject
            {
                ["opened"] = longLived.Opened,
                ["messages"] = longLived.Messages,
                ["unexpectedCloses"] = longLived.UnexpectedCloses,
                ["idleCutSeconds"] = JsonSerializer.SerializeToNode(idle),
                ["idleTimeoutSeconds"] = SoakConfig.WebSocketIdleSeconds,
            },
            ["slowClients"] = new JsonObject
            {
                ["slowlorisCutSeconds"] = JsonSerializer.SerializeToNode(slowloris),
                ["slowlorisSurvived"] = longLived.SlowlorisSurvived,
                ["slowBodyCutSeconds"] = JsonSerializer.SerializeToNode(slowBody),
            },
            ["admin"] = new JsonObject { ["calls"] = poller.Calls, ["errors"] = poller.Errors },
            ["mcp"] = new JsonObject { ["enabled"] = options.Mcp, ["failures"] = mcp.Failures, ["calls"] = JsonSerializer.SerializeToNode(mcp.Calls) },
            ["browser"] = new JsonObject
            {
                ["mode"] = browser.Mode,
                ["checks"] = browser.Checks,
                ["stale"] = browser.Stale,
                ["reloads"] = browser.Reloads,
                ["problems"] = JsonSerializer.SerializeToNode(browser.Problems),
            },
            ["pow"] = new JsonObject { ["solves"] = mix.PowSolves },
            ["generator"] = new JsonObject { ["skipped"] = Interlocked.Read(ref recorder.TotalSkipped), ["samples"] = sampler.Samples },
            ["upstreams"] = JsonSerializer.SerializeToNode(config.AllNodes.Select(n => new { n.Name, n.Port, n.Requests, n.WebSocketsAccepted })),
            ["authProviderCalls"] = authProvider.Calls,
        };
        await File.WriteAllTextAsync(Path.Combine(options.OutDirectory, "facts.json"), root.ToJsonString(indented));
        await File.WriteAllTextAsync(Path.Combine(options.OutDirectory, "publishes.json"), JsonSerializer.Serialize(publisher.Log));
    }
}

static JsonObject Window(Histogram histogram) => new()
{
    ["count"] = histogram.Count,
    ["p50Ms"] = Round(histogram.Percentile(0.50) / 1000),
    ["p99Ms"] = Round(histogram.Percentile(0.99) / 1000),
    ["p999Ms"] = Round(histogram.Percentile(0.999) / 1000),
};

static double? Round(double value) => double.IsNaN(value) ? null : Math.Round(value, 3);

static async Task<JsonObject> Snapshot(AdminClient admin)
{
    var analytics = await admin.GetAsync("/api/v1/analytics?range=hour");
    var overview = await admin.GetAsync("/api/v1/metrics/overview?range=hour");
    JsonObject result = new() { ["at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) };
    if (analytics.Ok && analytics.Json is JsonObject a)
    {
        result["analytics"] = new JsonObject { ["totals"] = a["totals"]?.DeepClone(), ["series"] = a["series"]?.DeepClone() };
    }

    if (overview.Ok && overview.Json is JsonObject o)
    {
        result["overviewSeries"] = o["series"]?.DeepClone();
    }

    return result;
}

static async Task<bool> WaitForHealthyAsync(AdminClient admin, CancellationToken ct, bool quiet = false)
{
    var watch = Stopwatch.StartNew();
    while (watch.Elapsed < TimeSpan.FromSeconds(90))
    {
        try
        {
            var health = await admin.GetAsync("/api/v1/upstreams/health", ct);
            if (health.Ok && health.Json is JsonArray upstreams && upstreams.Count > 0
                && upstreams.All(u => u!["destinations"]!.AsArray().All(d => d!["state"]!.GetValue<string>() is "healthy")))
            {
                return true;
            }
        }
        catch (HttpRequestException)
        {
        }

        await Task.Delay(500, ct);
    }

    if (!quiet)
    {
        throw new TimeoutException("Not every destination became healthy within 90 s.");
    }

    return false;
}

internal sealed class Facts
{
    public JsonNode? Flap { get; set; }

    public JsonNode? Restart { get; set; }

    public JsonNode? Final { get; set; }

    public JsonNode? FinalStop { get; set; }
}
