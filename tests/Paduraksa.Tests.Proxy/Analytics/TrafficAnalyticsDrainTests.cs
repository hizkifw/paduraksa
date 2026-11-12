using System.Net;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Proxy.Analytics;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Analytics;

/// <summary>The analytics as the drain's third consumer: fed after the file, failing alone, flushed at shutdown, and never fed by a host that logs nothing.</summary>
public sealed class TrafficAnalyticsDrainTests(TestUpstream upstream) : IClassFixture<TestUpstream>
{
    private static SnapshotStore StoreWith(params string[] labels)
    {
        SnapshotStore store = new();
        store.Publish(Bound(new ProxyConfigModel
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:9/")],
            Hosts = [.. labels.Select(static label => ProxyHost(label, label + ".test", Route(label + "-root", "web")))],
        }));
        return store;
    }

    private sealed class CapturingSink : IAccessLogSink
    {
        public List<string> Lines { get; } = [];

        public bool Flushed { get; private set; }

        public void WriteLine(ReadOnlySpan<char> line) => Lines.Add(line.ToString());

        public void Flush() => Flushed = true;
    }

    private sealed class RecordingLogger : ILogger<AccessLogDrainService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    /// <summary>A clock that stops working on demand: what makes the analytics' tick throw.</summary>
    private sealed class FlakyClock : TimeProvider
    {
        public bool Broken { get; set; }

        public override long GetTimestamp() => Broken ? throw new InvalidOperationException("the clock is gone") : Environment.TickCount64 * 1_000;

        public override long TimestampFrequency => 1_000_000;
    }

    [Fact]
    public void TheDrainFeedsTheAnalyticsAfterTheFileAndTheyTickOnEveryPass()
    {
        AccessLogRing ring = new(16);
        CapturingSink sink = new();
        AnalyticsBacklog backlog = new();
        TrafficAnalytics analytics = new(StoreWith("app"), TimeProvider.System, backlog);
        AccessLogDrainService drain = new(ring, sink, AccessLogFormatOptions.Default, NullLogger<AccessLogDrainService>.Instance, analytics: analytics);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(ring.TryWrite(new() { TimestampUtcTicks = DateTime.UtcNow.Ticks, Host = "app.test", Method = "GET", Path = "/p" + i, Protocol = "HTTP/1.1", StatusCode = 200, UserAgent = "curl/8" }));
        }

        drain.Drain();

        Assert.Equal(3, sink.Lines.Count);
        Assert.Equal(3, analytics.Counted);
        Assert.Equal(3, Assert.Single(analytics.Live(), static live => live.Subject == AnalyticsSubject.Host).Bucket!.Requests);
        drain.Dispose();
    }

    [Fact]
    public void AnAnalyticsThatFailsIsReportedAndTheFileStillGetsEveryLine()
    {
        AccessLogRing ring = new(16);
        CapturingSink sink = new();
        RecordingLogger logger = new();
        FlakyClock clock = new();
        TrafficAnalytics analytics = new(StoreWith("app"), clock, new AnalyticsBacklog());
        AccessLogDrainService drain = new(ring, sink, AccessLogFormatOptions.Default, logger, analytics: analytics);
        for (var i = 1; i <= 3; i++)
        {
            Assert.True(ring.TryWrite(new() { Method = "GET", Protocol = "HTTP/1.1", Path = "/p" + i, Host = "app.test" }));
        }

        clock.Broken = true;
        drain.Drain();

        Assert.Equal(3, sink.Lines.Count);
        Assert.Contains(logger.Messages, static message => message.Contains("access log isn't being written", StringComparison.Ordinal));
        // The entries were still counted: it is only the bucket clock that failed, and the next pass works again.
        clock.Broken = false;
        drain.Drain();
        Assert.Equal(3, analytics.Counted);
        drain.Dispose();
    }

    [Fact]
    public void AnEntryThatMakesTheAnalyticsThrowDoesNotCostTheTailOrTheFileALine()
    {
        // A stack of the three consumers: a bucket clock that fails at the first pass leaves the file and the tail whole.
        AccessLogRing ring = new(16);
        CapturingSink sink = new();
        AccessLogTail tail = new(8, AccessLogFormatOptions.Default);
        using var subscription = tail.Subscribe(AccessLogTailFilter.All, 0)!;
        FlakyClock clock = new() { Broken = false };
        TrafficAnalytics analytics = new(StoreWith("app"), clock, new AnalyticsBacklog());
        AccessLogDrainService drain = new(ring, sink, AccessLogFormatOptions.Default, new RecordingLogger(), tail, analytics);
        Assert.True(ring.TryWrite(new() { Method = "GET", Protocol = "HTTP/1.1", Path = "/x", Host = "app.test" }));
        clock.Broken = true;

        drain.Drain();

        Assert.Single(sink.Lines);
        Assert.True(subscription.Reader.TryRead(out var seen));
        Assert.Equal("/x", seen.Path);
        drain.Dispose();
    }

    [Fact]
    public async Task AStreamOfUniqueAgentsCostsTheFileNoLinesAndTheAnalyticsNoEntries()
    {
        // The cache's worst case: every agent is new and 1 KiB long, so each entry is a full classification, and the file must still get every
        // line and the analytics every entry. The writer waits for room instead of keeping a rate: how fast the drain goes is the benchmark's
        // question (a loaded runner starves it and the ring overflows, which says nothing about the drain), what it must never do is lose or
        // skip an entry.
        AccessLogRing ring = new(AccessLogRing.DefaultCapacity);
        CapturingSink sink = new();
        TrafficAnalytics analytics = new(StoreWith("app"), TimeProvider.System, new AnalyticsBacklog());
        using AccessLogDrainService drain = new(ring, sink, AccessLogFormatOptions.Default, NullLogger<AccessLogDrainService>.Instance, analytics: analytics);
        await drain.StartAsync(TestContext.Current.CancellationToken);
        var filler = new string('x', 900);
        const int Entries = 20_000;
        var deadline = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < Entries; i++)
        {
            AccessLogEntry entry = new() { TimestampUtcTicks = DateTime.UtcNow.Ticks, Host = "app.test", Method = "GET", Path = "/p", Protocol = "HTTP/1.1", StatusCode = 200, UserAgent = $"Mozilla/5.0 ({i}) {filler}" };
            while (!ring.TryWrite(entry))
            {
                Assert.True(deadline.Elapsed < TimeSpan.FromMinutes(2), $"the drain stopped taking entries at {i}");
                await Task.Delay(1, TestContext.Current.CancellationToken);
            }
        }

        while (ring.EntriesRead < Entries)
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromMinutes(2), $"the drain read {ring.EntriesRead} of {Entries}");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        await drain.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Entries, sink.Lines.Count);
        Assert.Equal(Entries, analytics.Counted);
    }

    [Fact]
    public void AnAnalyticsTheDrainIsFarAheadOfIsNotAskedToClassifyNewAgents()
    {
        AccessLogRing ring = new(AccessLogRing.MinimumCapacity);
        CapturingSink sink = new();
        TrafficAnalytics analytics = new(StoreWith("app"), TimeProvider.System, new AnalyticsBacklog());
        AccessLogDrainService drain = new(ring, sink, AccessLogFormatOptions.Default, NullLogger<AccessLogDrainService>.Instance, analytics: analytics);
        for (var i = 0; i < 800; i++)
        {
            Assert.True(ring.TryWrite(new() { TimestampUtcTicks = DateTime.UtcNow.Ticks, Host = "app.test", Method = "GET", Path = "/p", Protocol = "HTTP/1.1", StatusCode = 200, UserAgent = "Mozilla/5.0 unique-" + i }));
        }

        drain.Drain();

        // 800 queued in a ring of 1 024: the drain was past half full for the first 288 it read, and those new agents were left unclassified.
        var bucket = Assert.Single(analytics.Live(), static live => live.Subject == AnalyticsSubject.Host).Bucket!;
        Assert.Equal(800, sink.Lines.Count);
        Assert.Equal(800, bucket.Requests);
        Assert.InRange(bucket.Browsers.Counts["Unclassified"], 280, 290);
        Assert.Equal(800, bucket.Browsers.Total);
        drain.Dispose();
    }

    [Fact]
    public async Task StoppingTheServiceClosesTheOpenBucketIntoTheBacklog()
    {
        AccessLogRing ring = new(16);
        AnalyticsBacklog backlog = new();
        TrafficAnalytics analytics = new(StoreWith("app"), TimeProvider.System, backlog);
        AccessLogDrainService service = new(ring, new CapturingSink(), AccessLogFormatOptions.Default, NullLogger<AccessLogDrainService>.Instance, analytics: analytics);
        await service.StartAsync(TestContext.Current.CancellationToken);
        // The service is running once it has drained something (stopping a service that has not started its loop yet is a race of the host's, not of ours).
        Assert.True(ring.TryWrite(new() { TimestampUtcTicks = DateTime.UtcNow.Ticks, Host = "app.test", Method = "GET", Path = "/live", Protocol = "HTTP/1.1", StatusCode = 200 }));
        for (var attempt = 0; attempt < 1_000 && analytics.Counted < 1; attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, analytics.Counted);
        Assert.True(ring.TryWrite(new() { TimestampUtcTicks = DateTime.UtcNow.Ticks, Host = "app.test", Method = "GET", Path = "/at-shutdown", Protocol = "HTTP/1.1", StatusCode = 200 }));

        await service.StopAsync(TestContext.Current.CancellationToken);

        var rollup = Assert.Single(backlog.Unwritten(), static rollup => rollup.Subject == AnalyticsSubject.Host);
        Assert.Equal(2, rollup.Requests);
        Assert.Empty(analytics.Live());
        service.Dispose();
    }

    [Fact]
    public async Task AHostWithTheLogOffIsInNeitherTheFileNorTheAnalyticsThroughARealListener()
    {
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", upstream.Address("a"))],
            Hosts =
            [
                ProxyHost("app", "app.test", Route("app-root", "web")),
                ProxyHost("off", "quiet.test", Route("off-root", "web")) with { AccessLog = false },
            ],
        };
        await using var server = await ProxyTestServer.StartAsync(model);
        AnalyticsBacklog backlog = new();
        TrafficAnalytics analytics = new(server.Store, TimeProvider.System, backlog);
        CapturingSink sink = new();
        AccessLogDrainService drain = new(server.AccessLog, sink, AccessLogFormatOptions.Default, NullLogger<AccessLogDrainService>.Instance, analytics: analytics);
        using var client = ProxyTestServer.CreateClient(maxConnections: 1);
        var ct = TestContext.Current.CancellationToken;

        using (var known = await client.SendAsync(server.Request("app.test", "/api/items?token=hunter2", HttpMethod.Get), ct))
        {
            Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        }

        using (var quiet = await client.SendAsync(server.Request("quiet.test", "/hidden"), ct))
        {
            Assert.Equal(HttpStatusCode.OK, quiet.StatusCode);
        }

        using (var nobody = await client.SendAsync(server.Request("nobody.test", "/scan"), ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, nobody.StatusCode);
        }

        for (var attempt = 0; attempt < 2_000 && server.AccessLog.EntriesWritten < 2; attempt++)
        {
            await Task.Delay(1, ct);
        }

        drain.Drain();
        drain.Flush();

        // A stray request to the listener (a port prober on a developer machine) may be in the stream too: the test reads its own.
        var hosts = backlog.Unwritten().Where(static rollup => rollup.Subject == AnalyticsSubject.Host).ToList();
        var app = Assert.Single(hosts);
        Assert.Equal(TestIds.Host("app").ToString(), app.HostKey);
        var decoded = TrafficBucketCodec.Decode(app.Bucket!)!;
        Assert.Contains(decoded.Paths.Top(10), static hitter => hitter.Key == "/api/items");
        Assert.DoesNotContain(decoded.Paths.Top(10), static hitter => hitter.Key.Contains("hunter2", StringComparison.Ordinal) || hitter.Key.Contains('?', StringComparison.Ordinal));
        Assert.DoesNotContain(backlog.Unwritten().Where(static rollup => rollup.Bucket is not null).SelectMany(static rollup => TrafficBucketCodec.Decode(rollup.Bucket!)!.Paths.Top(100)), static hitter => hitter.Key == "/hidden");
        var unmatched = Assert.Single(backlog.Unwritten(), static rollup => rollup.Subject == AnalyticsSubject.Unmatched);
        Assert.Contains(TrafficBucketCodec.Decode(unmatched.Bucket!)!.Paths.Top(10), static hitter => hitter.Key == "/scan");
        Assert.DoesNotContain(sink.Lines, static line => line.Contains("/hidden", StringComparison.Ordinal));
        drain.Dispose();
    }
}
