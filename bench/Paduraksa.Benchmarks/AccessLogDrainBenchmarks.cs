using BenchmarkDotNet.Attributes;

using Microsoft.Extensions.Logging.Abstractions;

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
    private AccessLogEntry _entry;

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

    private void Pass(AccessLogDrainService drain)
    {
        for (var i = 0; i < Entries; i++)
        {
            _ring.TryWrite(_entry);
        }

        drain.Drain();
    }
}
