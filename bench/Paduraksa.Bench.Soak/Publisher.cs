using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text.Json.Nodes;

using Paduraksa.Core.Model;

namespace Paduraksa.Bench.Soak;

/// <summary>One config change as the run saw it.</summary>
internal sealed record PublishRecord(double T, string Kind, double Ms, double? LiveMs, int Status, long? Generation, string? Error, bool Burst);

/// <summary>
/// The config changes. Every ten seconds one edit through the admin API, cycling the four kinds the brief names (a host, an upstream, a rate-limit policy, an access list),
/// and, in the burst, an edit of the canary host every 100 ms. A change that touches the canary also checks that the new revision is what the proxy answers with, and how
/// long after the command that was. The canary's header carries a counter that only goes up, so the clients can tell a snapshot going backwards.
/// </summary>
internal sealed class Publisher(SoakOptions options, SoakConfig config, ResourceStore store, Events events, Recorder recorder)
{
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly List<PublishRecord> _log = [];
    private readonly HttpClient _live = MakeLive(options);
    private readonly int[] _count = new int[4];
    private long _revision;
    private int _cycle;
    private bool _spareAdded;

    public IReadOnlyList<PublishRecord> Log
    {
        get
        {
            lock (_log)
            {
                return [.. _log];
            }
        }
    }

    public long Revision => Interlocked.Read(ref _revision);

    private static HttpClient MakeLive(SoakOptions options) => new(new SocketsHttpHandler
    {
        ConnectCallback = async (context, ct) => await Loopback.ConnectAsync(context.DnsEndPoint.Port, ct),
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = static (_, _, _, _) => true },
        AutomaticDecompression = DecompressionMethods.None,
    })
    {
        BaseAddress = new Uri($"https://{SoakConfig.Name("canary")}:{options.PublicHttps}"),
        Timeout = TimeSpan.FromSeconds(5),
    };

    public (int Count, double MeanMs, double MaxMs) Since(double from)
    {
        var rows = Log.Where(r => r.T >= from).ToList();
        return rows.Count == 0 ? (0, 0, 0) : (rows.Count, rows.Average(r => r.Ms), rows.Max(r => r.Ms));
    }

    /// <summary>Every ten seconds, from the load's start, for the whole run.</summary>
    public async Task RunCycleAsync(CancellationToken ct)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await PublishAsync(_cycle++ % 4, burst: false, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task RunBurstAsync(int seconds, CancellationToken ct)
    {
        events.Write("burst.begin", new { seconds });
        var watch = Stopwatch.StartNew();
        long issued = 0;
        long slipped = 0;
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(100));
        try
        {
            while (watch.Elapsed.TotalSeconds < seconds && await timer.WaitForNextTickAsync(ct))
            {
                // A publish still running when the next is due is a slip, and says how slow publishing got; the next one waits its turn.
                if (_one.CurrentCount == 0)
                {
                    slipped++;
                    continue;
                }

                issued++;
                await PublishAsync(0, burst: true, ct, canaryOnly: true);
            }
        }
        catch (OperationCanceledException)
        {
        }

        events.Write("burst.end", new { issued, slipped, seconds = Math.Round(watch.Elapsed.TotalSeconds, 1) });
    }

    private async Task PublishAsync(int kind, bool burst, CancellationToken ct, bool canaryOnly = false)
    {
        await _one.WaitAsync(ct);
        try
        {
            var t = events.ElapsedSeconds;
            // The edit is numbered per kind, so every one changes something: a publish that finds nothing changed is not a publish.
            var n = _count[kind]++;
            string name;
            Resource resource;
            long expectedRevision = 0;
            var canary = false;
            switch (kind)
            {
                case 0:
                    // Alternate cycles edit the canary (a proxied, checkable change) and an ordinary host (a real edit, nothing to see in the answer).
                    canary = burst || canaryOnly || n % 2 == 0;
                    if (canary)
                    {
                        name = "host:canary";
                        resource = store.Get("hosts", SoakConfig.Name("canary"));
                        expectedRevision = Interlocked.Increment(ref _revision);
                        resource.Body["settings"]!["headers"]!["response"]![0]!["value"] = expectedRevision.ToString();
                    }
                    else
                    {
                        var host = config.AppNames[Random.Shared.Next(config.AppNames.Count)];
                        name = "host:" + host;
                        resource = store.Get("hosts", host);
                        resource.Body["notes"] = "edit " + n;
                        resource.Body["tags"] = new JsonArray(Random.Shared.Next(2) == 0 ? "soak" : "soak-b");
                    }

                    break;
                case 1:
                    name = "upstream:web-b";
                    resource = store.Get("upstreams", "web-b");
                    EditUpstream(resource);
                    break;
                case 2:
                    name = "rate-limit:mid";
                    resource = store.Get("rate-limit-policies", "mid");
                    resource.Body["requests"] = SoakConfig.MidPerSecond + (n % 2 == 0 ? 50 : 0);
                    break;
                default:
                    name = "access-list:lan";
                    resource = store.Get("access-lists", "lan");
                    resource.Body["ip"]!["allow"] = new JsonArray(SoakConfig.LanAllow((n + 1) % 4).Select(a => (JsonNode)a).ToArray());
                    break;
            }

            AdminResult result;
            try
            {
                result = await store.PutAsync(resource, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                Add(new PublishRecord(t, name, 0, null, 0, null, Recorder.KindOf(ex) + ": " + ex.Message, burst), recorder.Tolerance.Applies(t, "admin"));
                return;
            }

            long? generation = result.Json?["publish"]?["generation"]?.GetValue<long>();
            var published = result.Ok && result.Json?["publish"]?["succeeded"]?.GetValue<bool>() == true;
            double? liveMs = null;
            if (published && canary && !burst)
            {
                liveMs = await WaitForLiveAsync(expectedRevision, result.Milliseconds, ct);
            }

            Add(
                new PublishRecord(t, name, Math.Round(result.Milliseconds, 2), liveMs, (int)result.Status, generation, published ? null : result.Body[..Math.Min(300, result.Body.Length)], burst),
                tolerated: false);
        }
        finally
        {
            _one.Release();
        }
    }

    private void EditUpstream(Resource resource)
    {
        var destinations = resource.Body["destinations"]!.AsArray();
        var spare = config.Upstreams.First(u => u.Name == "web-b").Spare!;
        if (_spareAdded)
        {
            for (var i = destinations.Count - 1; i >= 0; i--)
            {
                if (destinations[i]!["address"]!.GetValue<string>().EndsWith(":" + spare.Port, StringComparison.Ordinal))
                {
                    destinations.RemoveAt(i);
                }
            }
        }
        else
        {
            destinations.Add(new JsonObject { ["id"] = DestinationId.New().ToString(), ["address"] = $"http://127.0.0.1:{spare.Port}", ["enabled"] = true });
        }

        _spareAdded = !_spareAdded;
        resource.Body["loadBalancing"] = _spareAdded ? "powerOfTwoChoices" : "roundRobin";
    }

    /// <summary>How long after the command the canary answers with the new revision: the command-to-live figure.</summary>
    private async Task<double?> WaitForLiveAsync(long revision, double apiMs, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(3))
        {
            try
            {
                using var response = await _live.GetAsync("/c", ct);
                if (response.Headers.TryGetValues(SoakConfig.RevisionHeader, out var values) && long.TryParse(values.First(), out var seen) && seen >= revision)
                {
                    // The API call returned after the publish; the live check begins after that, so its time is added to the call's.
                    return Math.Round(apiMs + watch.Elapsed.TotalMilliseconds, 2);
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(2, ct);
        }

        return null;
    }

    private void Add(PublishRecord record, bool tolerated)
    {
        lock (_log)
        {
            _log.Add(record);
        }

        if (record.Error is not null && !tolerated)
        {
            events.Write("publish.failed", record);
        }
    }
}
