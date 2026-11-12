using BenchmarkDotNet.Attributes;

using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Proxy.Analytics;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Benchmarks;

/// <summary>
/// One pass of <see cref="AccessLogDrainService"/> over a ring of 1 024 entries, per entry: the file's line alone, with the live
/// tail's buffer and no subscriber (what an install that nobody watches pays), with a subscriber that keeps up, and with one that
/// never reads (its channel overflows on every entry). The ring writes are part of every case, so the differences are the tail's.
/// </summary>
[MemoryDiagnoser]
public class AccessLogDrainBenchmarks : IDisposable
{
    private const int Entries = 1_024;

    private AccessLogRing _ring = null!;
    private AccessLogDrainService _fileOnly = null!;
    private AccessLogDrainService _withTail = null!;
    private AccessLogDrainService _withSubscriber = null!;
    private AccessLogDrainService _withSlowSubscriber = null!;
    private AccessLogTailSubscription _reading = null!;
    private AccessLogTailSubscription _slow = null!;
    private AccessLogTail _tailForReader = null!;
    private AccessLogDrainService _withAnalytics = null!;
    private AccessLogDrainService _withAnalyticsOnVariedTraffic = null!;
    private AccessLogDrainService _withAnalyticsOnUniqueAgents = null!;
    private string[] _uniqueAgents = [];
    private AccessLogEntry _entry;
    private AccessLogEntry[] _varied = [];
    private int _next;

    [GlobalSetup]
    public void Setup()
    {
        var store = new SnapshotStore();
        store.Publish(BenchConfig.Snapshot.Bind(1, [.. BenchConfig.Snapshot.RouteIdBySlot.Select(id => new Microsoft.AspNetCore.Http.Endpoint(static _ => Task.CompletedTask, Microsoft.AspNetCore.Http.EndpointMetadataCollection.Empty, id.ToString()))]));
        var options = AccessLogFormatOptions.Default;
        var sink = NullAccessLogSink.Instance;
        _ring = new AccessLogRing(Entries * 2);
        _entry = new AccessLogEntry
        {
            TimestampUtcTicks = DateTime.UtcNow.Ticks,
            ClientAddress = "198.51.100.7",
            Host = "app.test",
            Method = "GET",
            Path = "/api/v1/items/42",
            Query = "?page=2&token=hunter2",
            Protocol = "HTTP/2",
            StatusCode = 200,
            ResponseBytes = 512,
            TotalTicks = 12_340,
            UpstreamTicks = 9_870,
            UpstreamAddress = "http://10.0.0.2:8080/",
            RequestId = "0k3m9x2p-00000000000abc",
            Referer = "https://example.test/",
            UserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36",
        };

        _fileOnly = new(_ring, sink, options, NullLogger<AccessLogDrainService>.Instance);
        _withTail = new(_ring, sink, options, NullLogger<AccessLogDrainService>.Instance, new AccessLogTail(4_096, options, store));
        _tailForReader = new AccessLogTail(4_096, options, store);
        _reading = _tailForReader.Subscribe(AccessLogTailFilter.All, 0, channelCapacity: Entries * 2)!;
        _withSubscriber = new(_ring, sink, options, NullLogger<AccessLogDrainService>.Instance, _tailForReader);
        var slowTail = new AccessLogTail(4_096, options, store);
        _slow = slowTail.Subscribe(AccessLogTailFilter.All, 0, channelCapacity: 16)!;
        _withSlowSubscriber = new(_ring, sink, options, NullLogger<AccessLogDrainService>.Instance, slowTail);
        _withAnalytics = new(_ring, sink, options, NullLogger<AccessLogDrainService>.Instance, analytics: new TrafficAnalytics(store, TimeProvider.System, new AnalyticsBacklog()));
        _withAnalyticsOnVariedTraffic = new(_ring, sink, options, NullLogger<AccessLogDrainService>.Instance, analytics: new TrafficAnalytics(store, TimeProvider.System, new AnalyticsBacklog()));
        _withAnalyticsOnUniqueAgents = new(_ring, sink, options, NullLogger<AccessLogDrainService>.Instance, analytics: new TrafficAnalytics(store, TimeProvider.System, new AnalyticsBacklog()));
        // The cache's worst case, a flood of agents it has never seen: 1 KiB each, no token in them, so every entry is a full classification.
        _uniqueAgents = new string[1_024 * 16];
        for (var i = 0; i < _uniqueAgents.Length; i++)
        {
            _uniqueAgents[i] = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Zq" + i + "xv/1.0 ").PadRight(1_024, 'q');
        }

        string[] agents =
        [
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1",
            "Mozilla/5.0 (X11; Linux x86_64; rv:133.0) Gecko/20100101 Firefox/133.0",
            "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
            "curl/8.5.0",
        ];
        _varied = new AccessLogEntry[256];
        for (var i = 0; i < _varied.Length; i++)
        {
            _varied[i] = _entry with
            {
                ClientAddress = $"198.51.{i % 100}.{i}",
                Path = "/page/" + (i % 97),
                StatusCode = i % 17 == 0 ? 404 : i % 41 == 0 ? 502 : 200,
                UserAgent = agents[i % agents.Length],
                Referer = i % 3 == 0 ? $"https://site{i % 11}.example/x?q=1" : null,
                ResponseBytes = 200 + (i * 37),
            };
        }
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _reading.Dispose();
        _slow.Dispose();
        _fileOnly.Dispose();
        _withTail.Dispose();
        _withSubscriber.Dispose();
        _withSlowSubscriber.Dispose();
        _withAnalytics.Dispose();
        _withAnalyticsOnVariedTraffic.Dispose();
        _withAnalyticsOnUniqueAgents.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Entries)]
    public void FileOnly() => Pass(_fileOnly);

    [Benchmark(OperationsPerInvoke = Entries)]
    public void TailNobodyWatching() => Pass(_withTail);

    [Benchmark(OperationsPerInvoke = Entries)]
    public void TailWithAReader()
    {
        Pass(_withSubscriber);
        while (_reading.Reader.TryRead(out _))
        {
        }
    }

    [Benchmark(OperationsPerInvoke = Entries)]
    public void TailWithASlowReader() => Pass(_withSlowSubscriber);

    [Benchmark(OperationsPerInvoke = Entries)]
    public void AnalyticsSameEntry() => Pass(_withAnalytics);

    [Benchmark(OperationsPerInvoke = Entries)]
    public void AnalyticsVariedTraffic()
    {
        for (var i = 0; i < Entries; i++)
        {
            _ring.TryWrite(_varied[_next++ & (_varied.Length - 1)]);
        }

        _withAnalyticsOnVariedTraffic.Drain();
    }

    [Benchmark(OperationsPerInvoke = Entries)]
    public void AnalyticsUniqueAgentsEveryEntryAMiss()
    {
        for (var i = 0; i < Entries; i++)
        {
            _ring.TryWrite(_entry with { UserAgent = _uniqueAgents[_nextAgent++ & (_uniqueAgents.Length - 1)] });
        }

        _withAnalyticsOnUniqueAgents.Drain();
    }

    private int _nextAgent;

    private void Pass(AccessLogDrainService drain)
    {
        for (var i = 0; i < Entries; i++)
        {
            _ring.TryWrite(_entry);
        }

        drain.Drain();
    }
}
