using System.Text;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Analytics;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Analytics;

/// <summary>The drain's third consumer: which bucket and host an entry lands in, when a bucket closes, what coverage says, and what is never kept.</summary>
public sealed class TrafficAnalyticsTests
{
    // Five-minute boundaries line up at this instant.
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    private static readonly byte[] Salt = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

    internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private long _timestamp = start.UtcTicks;

        public DateTimeOffset Now { get; private set; } = start;

        public void Advance(TimeSpan by)
        {
            Now += by;
            _timestamp += by.Ticks;
        }

        /// <summary>Time passes without anyone looking: the wall clock moves, nothing ticks in between.</summary>
        public override DateTimeOffset GetUtcNow() => Now;

        public override long GetTimestamp() => _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    private sealed class Rig
    {
        public Rig(ProxyConfigModel? model = null, byte[]? salt = null)
        {
            Store = new SnapshotStore();
            Store.Publish(Bound(model ?? Hosts("app", "other")));
            Clock = new ManualClock(Start);
            Analytics = new TrafficAnalytics(Store, Clock, Backlog, salt ?? Salt);
        }

        public SnapshotStore Store { get; }

        public ManualClock Clock { get; }

        public AnalyticsBacklog Backlog { get; } = new();

        public TrafficAnalytics Analytics { get; }

        public AccessLogEntry Entry(string host = "app.test", string path = "/", int status = 200, string client = "198.51.100.7", string userAgent = "curl/8.5.0", long bytes = 100, TimeSpan? after = null) => new()
        {
            TimestampUtcTicks = (Clock.Now + (after ?? TimeSpan.Zero)).UtcTicks,
            Host = host,
            Path = path,
            Method = "GET",
            Protocol = "HTTP/1.1",
            StatusCode = status,
            ClientAddress = client,
            UserAgent = userAgent,
            ResponseBytes = bytes,
            TotalTicks = TimeSpan.TicksPerMillisecond * 4,
        };

        public void Offer(AccessLogEntry entry) => Analytics.Offer(in entry);

        /// <summary>Time passes in one-second ticks, as the drain's passes would see it.</summary>
        public void Run(TimeSpan span)
        {
            for (var elapsed = TimeSpan.Zero; elapsed < span; elapsed += TimeSpan.FromSeconds(1))
            {
                Clock.Advance(TimeSpan.FromSeconds(1));
                Analytics.Tick();
            }
        }

        public IReadOnlyList<AnalyticsRollup> Closed() => Backlog.Unwritten();
    }

    private static ProxyConfigModel Hosts(params string[] labels) => new()
    {
        Upstreams = [Upstream("web", "http://127.0.0.1:9/")],
        Hosts = [.. labels.Select(static label => ProxyHost(label, label + ".test", Route(label + "-root", "web")))],
    };

    [Fact]
    public void AnEntryIsCountedInItsHostsBucketAndClosesWhenItsTimeIsUp()
    {
        var rig = new Rig();
        rig.Offer(rig.Entry("app.test", "/a"));
        rig.Offer(rig.Entry("app.test", "/b", status: 500));
        rig.Offer(rig.Entry("other.test", "/c"));
        rig.Run(TimeSpan.FromSeconds(10));

        Assert.Empty(rig.Closed());

        // The bucket's end, plus the grace the ring gets to be read.
        rig.Run(TimeSpan.FromMinutes(5) + TrafficAnalytics.CloseGrace);

        var closed = rig.Closed();
        var app = Assert.Single(closed, rollup => rollup.HostKey == TestIds.Host("app").ToString());
        var other = Assert.Single(closed, rollup => rollup.HostKey == TestIds.Host("other").ToString());
        Assert.Equal((AnalyticsSubject.Host, Start.ToUnixTimeSeconds(), 2L, 200L, 1L), (app.Subject, app.Start, app.Requests, app.BytesOut, app.ServerErrors));
        Assert.Equal(1, other.Requests);
        var decoded = TrafficBucketCodec.Decode(app.Bucket!)!;
        Assert.Equal(["/a", "/b"], decoded.Paths.Top(5).Select(static h => h.Key).Order());
    }

    [Fact]
    public void RequestsNoHostOwnsHaveABucketOfTheirOwn()
    {
        var rig = new Rig();
        rig.Offer(rig.Entry("nobody.test", "/wp-login.php", status: 404));
        rig.Offer(rig.Entry(host: "203.0.113.9:8443", path: "/scan", status: 404));
        rig.Offer(rig.Entry() with { Host = null });
        rig.Run(TimeSpan.FromMinutes(6));

        var unmatched = Assert.Single(rig.Closed(), static rollup => rollup.Subject == AnalyticsSubject.Unmatched);

        Assert.Equal(string.Empty, unmatched.HostKey);
        Assert.Equal(3, unmatched.Requests);
    }

    [Fact]
    public void AnEntryIsFiledByItsOwnTimeNotByTheTimeTheDrainReachedIt()
    {
        var rig = new Rig();
        rig.Run(TimeSpan.FromSeconds(1));
        rig.Offer(rig.Entry(after: TimeSpan.FromMinutes(-2)));
        rig.Offer(rig.Entry(after: TimeSpan.FromMinutes(4)));
        rig.Run(TimeSpan.FromMinutes(11));

        var starts = rig.Closed().Where(static rollup => rollup.Subject == AnalyticsSubject.Host).Select(static rollup => rollup.Start).Order().ToList();

        // 11:58 is in the 11:55 bucket and 12:04 in the 12:00 one, whenever the drain got to them.
        Assert.Equal([Start.ToUnixTimeSeconds() - 300, Start.ToUnixTimeSeconds()], starts);
    }

    [Fact]
    public void ABucketAnEntryArrivesForAfterItClosedGoesToTheOldestOpenOne()
    {
        var rig = new Rig();
        rig.Run(TimeSpan.FromMinutes(5) + TrafficAnalytics.CloseGrace + TimeSpan.FromSeconds(1));
        rig.Offer(rig.Entry(after: TimeSpan.FromMinutes(-6)));
        rig.Run(TimeSpan.FromMinutes(5));

        var late = Assert.Single(rig.Closed(), static rollup => rollup.Subject == AnalyticsSubject.Host);

        Assert.Equal(Start.ToUnixTimeSeconds() + 300, late.Start);
    }

    [Fact]
    public void ABucketTheProcessWatchedIsCoveredWholeAndAQuietOneIsStillThere()
    {
        var rig = new Rig();
        rig.Run(TimeSpan.FromMinutes(11));

        var coverage = rig.Closed().Where(static rollup => rollup.Subject == AnalyticsSubject.Coverage).OrderBy(static rollup => rollup.Start).ToList();

        // 12:00 and 12:05 closed (12:10's is still open), with no traffic and every second observed: idle, not a gap.
        Assert.Equal([Start.ToUnixTimeSeconds(), Start.ToUnixTimeSeconds() + 300], coverage.Select(static rollup => rollup.Start));
        Assert.All(coverage, rollup => Assert.Equal(300, rollup.CoveredSeconds));
        Assert.DoesNotContain(rig.Closed(), static rollup => rollup.Subject != AnalyticsSubject.Coverage);
    }

    [Fact]
    public void TimeNobodyWasWatchingIsNotCovered()
    {
        var rig = new Rig();
        rig.Run(TimeSpan.FromMinutes(5));
        // The process is suspended for twenty minutes: the clock moves, no drain pass runs.
        rig.Clock.Advance(TimeSpan.FromMinutes(20));
        rig.Analytics.Tick();
        rig.Run(TimeSpan.FromMinutes(6));

        var covered = rig.Closed().Where(static rollup => rollup.Subject == AnalyticsSubject.Coverage).Select(static rollup => rollup.Start).Order().ToList();

        // Buckets at 12:05 to 12:20 saw no tick; only the stretches before and after the suspension are covered.
        var gapStarts = Enumerable.Range(1, 3).Select(i => Start.ToUnixTimeSeconds() + (300 * (i + 1)));
        Assert.All(gapStarts, start => Assert.DoesNotContain(start, covered));
        Assert.Contains(Start.ToUnixTimeSeconds(), covered);
    }

    [Fact]
    public void ALongPauseThatBroughtEntriesCountsAsObserved()
    {
        // A drain that was late (a long GC, a starved pool) but whose ring held what happened meanwhile did observe the time.
        var rig = new Rig();
        rig.Run(TimeSpan.FromSeconds(30));
        rig.Offer(rig.Entry(after: TimeSpan.FromSeconds(20)));
        rig.Clock.Advance(TimeSpan.FromSeconds(60));
        rig.Analytics.Tick();
        rig.Run(TimeSpan.FromMinutes(5));

        var coverage = Assert.Single(rig.Closed(), static rollup => rollup.Subject == AnalyticsSubject.Coverage && rollup.Start == Start.ToUnixTimeSeconds());

        Assert.Equal(300, coverage.CoveredSeconds);
    }

    [Fact]
    public void FlushClosesTheOpenBucketWithOnlyTheSecondsItObserved()
    {
        var rig = new Rig();
        rig.Run(TimeSpan.FromSeconds(100));
        rig.Offer(rig.Entry());

        rig.Analytics.Flush();

        var rollups = rig.Closed();
        Assert.Equal(100, Assert.Single(rollups, static rollup => rollup.Subject == AnalyticsSubject.Coverage).CoveredSeconds);
        var host = Assert.Single(rollups, static rollup => rollup.Subject == AnalyticsSubject.Host);
        Assert.Equal(100, host.CoveredSeconds);
        Assert.Empty(rig.Analytics.Live());
    }

    [Fact]
    public void LiveShowsTheOpenBucketAsACopy()
    {
        var rig = new Rig();
        rig.Run(TimeSpan.FromSeconds(30));
        rig.Offer(rig.Entry("app.test", "/live"));

        var live = rig.Analytics.Live();
        rig.Offer(rig.Entry("app.test", "/later"));

        var app = Assert.Single(live, static bucket => bucket.Subject == AnalyticsSubject.Host);
        Assert.Equal(TestIds.Host("app"), app.Host);
        Assert.Equal(1, app.Bucket!.Requests);
        Assert.Equal(30, app.CoveredSeconds);
        Assert.Contains(live, static bucket => bucket.Subject == AnalyticsSubject.Coverage);
    }

    [Fact]
    public void AVisitorIsCountedOnceHoweverOftenItComesAndAnIPv6HostByItsPrefix()
    {
        var rig = new Rig();
        for (var i = 0; i < 20; i++)
        {
            rig.Offer(rig.Entry(client: "198.51.100.7"));
            rig.Offer(rig.Entry(client: "2001:db8:1:2:" + i.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + "::" + (i + 1).ToString("x", System.Globalization.CultureInfo.InvariantCulture)));
            rig.Offer(rig.Entry(client: "::ffff:198.51.100.7"));
        }

        rig.Offer(rig.Entry(client: "203.0.113.5"));
        rig.Offer(rig.Entry(client: "2001:db8:1:3::1"));
        rig.Offer(rig.Entry() with { ClientAddress = null });
        rig.Offer(rig.Entry(client: "not an address"));

        var visitors = Assert.Single(rig.Analytics.Live(), static bucket => bucket.Subject == AnalyticsSubject.Host).Bucket!.Visitors;

        // 198.51.100.7 (also as v4-mapped), one /64 of 2001:db8:1:2, 203.0.113.5, and 2001:db8:1:3: four.
        Assert.Equal(4, visitors.Estimate());
    }

    [Fact]
    public void NoAddressReachesTheStoredBytesAndAnotherProcessSaltMakesAnotherSketch()
    {
        Rig first = new(salt: Salt);
        Rig second = new(salt: [.. Salt.Select(static b => (byte)(b ^ 0x55))]);
        foreach (var rig in new[] { first, second })
        {
            for (var i = 1; i <= 50; i++)
            {
                rig.Offer(rig.Entry(client: "198.51.100." + i, userAgent: "agent/1.0"));
            }

            rig.Run(TimeSpan.FromMinutes(6));
        }

        var a = Assert.Single(first.Closed(), static rollup => rollup.Subject == AnalyticsSubject.Host).Bucket!;
        var b = Assert.Single(second.Closed(), static rollup => rollup.Subject == AnalyticsSubject.Host).Bucket!;

        // The same fifty visitors count as about fifty under either salt, in different registers: the salt, not the address, decides what is stored.
        Assert.InRange(TrafficBucketCodec.DecodeVisitors(a.Visitors)!.Estimate(), 47, 53);
        Assert.InRange(TrafficBucketCodec.DecodeVisitors(b.Visitors)!.Estimate(), 47, 53);
        Assert.NotEqual(a.Visitors, b.Visitors);
        var plain = Encoding.UTF8.GetString(TrafficBucketCodec.Decode(a) is { } bucket ? PlainBytes(bucket) : []);
        Assert.DoesNotContain("198.51.100", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("198.51.100", Encoding.Latin1.GetString(a.Visitors) + Encoding.Latin1.GetString(a.Data), StringComparison.Ordinal);
    }

    // Everything the stored form is made of, uncompressed, to look for what must not be in it.
    private static byte[] PlainBytes(TrafficBucket bucket)
    {
        using var stream = new MemoryStream();
        var encoded = TrafficBucketCodec.Encode(bucket);
        stream.Write(encoded.Visitors);
        // The data blob is Brotli; the tables' text is what matters, and it is rebuilt from the decoded bucket.
        foreach (var hitter in bucket.Paths.Top(1_000).Concat(bucket.Referers.Top(1_000)).Concat(bucket.UserAgents.Top(1_000)))
        {
            stream.Write(Encoding.UTF8.GetBytes(hitter.Key));
        }

        foreach (var label in bucket.Browsers.Counts.Keys.Concat(bucket.OperatingSystems.Counts.Keys).Concat(bucket.Methods.Counts.Keys).Concat(bucket.Statuses.Counts.Keys))
        {
            stream.Write(Encoding.UTF8.GetBytes(label));
        }

        return stream.ToArray();
    }

    private static ProxyConfigModel WithRobots(HostRobotsSpec robots, string label = "app") => new()
    {
        Upstreams = [Upstream("web", "http://127.0.0.1:9/")],
        Hosts = [ProxyHost(label, label + ".test", Route(label + "-root", "web")) with { Robots = robots }],
    };

    private static IReadOnlyDictionary<string, long> Ignored(Rig rig)
    {
        rig.Analytics.Flush();
        var host = Assert.Single(rig.Closed(), static rollup => rollup.Subject == AnalyticsSubject.Host);
        return TrafficBucketCodec.Decode(host.Bucket!)!.IgnoredRobots.Counts;
    }

    [Fact]
    public void ABotThatFetchesWhatARobotsTxtDisallowsIsCountedByName()
    {
        var rig = new Rig(WithRobots(new HostRobotsSpec { Mode = RobotsMode.DisallowAll }));
        rig.Offer(rig.Entry(path: "/page", userAgent: "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)"));
        rig.Offer(rig.Entry(path: "/page", userAgent: "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)"));
        rig.Offer(rig.Entry(path: "/other", userAgent: "GPTBot/1.2"));
        // The file itself is always allowed; a browser, a monitor and a library are not crawlers that should obey it.
        rig.Offer(rig.Entry(path: "/robots.txt", userAgent: "Googlebot/2.1"));
        rig.Offer(rig.Entry(path: "/page", userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"));
        rig.Offer(rig.Entry(path: "/page", userAgent: "UptimeRobot/2.0"));
        rig.Offer(rig.Entry(path: "/page", userAgent: "curl/8.5.0"));

        var ignored = Ignored(rig);

        Assert.Equal(2, ignored["SearchEngine|Googlebot"]);
        Assert.Equal(1, ignored["AiCrawler|GPTBot"]);
        Assert.Equal(2, ignored.Count);
    }

    [Fact]
    public void BlockingAiCrawlersOnlyMakesAnAiCrawlerAnOffender()
    {
        var rig = new Rig(WithRobots(new HostRobotsSpec { Mode = RobotsMode.BlockAiCrawlers }));
        rig.Offer(rig.Entry(path: "/page", userAgent: "Mozilla/5.0 (compatible; Googlebot/2.1)"));
        rig.Offer(rig.Entry(path: "/page", userAgent: "ClaudeBot/1.0"));

        var ignored = Ignored(rig);

        Assert.Equal(["AiCrawler|ClaudeBot"], ignored.Keys);
    }

    [Fact]
    public void ACustomFileIsReadAsServed()
    {
        var rig = new Rig(WithRobots(new HostRobotsSpec { Mode = RobotsMode.Custom, CustomText = "User-agent: *\nDisallow: /private/\nAllow: /private/open\n" }));
        rig.Offer(rig.Entry(path: "/private/x", userAgent: "SemrushBot/7"));
        rig.Offer(rig.Entry(path: "/private/open", userAgent: "SemrushBot/7"));
        rig.Offer(rig.Entry(path: "/public", userAgent: "SemrushBot/7"));

        var ignored = Ignored(rig);

        Assert.Equal(1, ignored["Unknown|SemrushBot"]);
    }

    [Fact]
    public void AHostThatServesNoRobotsTxtDisallowsNothing()
    {
        var rig = new Rig(WithRobots(HostRobotsSpec.Passthrough));
        rig.Offer(rig.Entry(path: "/page", userAgent: "GPTBot/1.2"));

        Assert.Empty(Ignored(rig));
    }

    [Fact]
    public void APublishedChangeToTheFileIsFollowed()
    {
        var rig = new Rig(WithRobots(new HostRobotsSpec { Mode = RobotsMode.BlockAiCrawlers }));
        rig.Offer(rig.Entry(path: "/page", userAgent: "Googlebot/2.1"));
        rig.Store.Publish(Bound(WithRobots(new HostRobotsSpec { Mode = RobotsMode.DisallowAll }), generation: 2));
        rig.Offer(rig.Entry(path: "/page", userAgent: "Googlebot/2.1"));

        var ignored = Ignored(rig);

        Assert.Equal(1, ignored["SearchEngine|Googlebot"]);
    }

    [Fact]
    public void ABurstOfDistinctHostsCannotGrowTheTableWithoutBound()
    {
        var labels = Enumerable.Range(0, TrafficAnalytics.MaxHosts + 50).Select(static i => "h" + i).ToArray();
        var rig = new Rig(Hosts(labels));
        foreach (var label in labels)
        {
            rig.Offer(rig.Entry(host: label + ".test"));
        }

        var live = rig.Analytics.Live().Where(static bucket => bucket.Subject != AnalyticsSubject.Coverage).ToList();

        Assert.True(live.Count <= TrafficAnalytics.MaxHosts + 1, $"{live.Count} host tables");
        Assert.Equal(labels.Length, live.Sum(static bucket => bucket.Bucket!.Requests));
        // What did not get a table of its own is counted with the requests no host owns, not lost.
        Assert.Contains(live, static bucket => bucket.Subject == AnalyticsSubject.Unmatched);
    }

    [Fact]
    public void ABehindDrainStillCountsEveryEntryButClassifiesOnlyAgentsItKnows()
    {
        var rig = new Rig(WithRobots(new HostRobotsSpec { Mode = RobotsMode.DisallowAll }));
        rig.Offer(rig.Entry(userAgent: "curl/8.5.0"));
        for (var i = 0; i < 50; i++)
        {
            var entry = rig.Entry(path: "/private", userAgent: "GPTBot/1.2 unique-" + i);
            rig.Analytics.Offer(in entry, behind: true);
        }

        var known = rig.Entry(userAgent: "curl/8.5.0");
        rig.Analytics.Offer(in known, behind: true);
        var bucket = Assert.Single(rig.Analytics.Live(), static live => live.Subject == AnalyticsSubject.Host).Bucket!;

        Assert.Equal(52, bucket.Requests);
        // The fifty unknown agents are counted as unclassified, with no bot and no robots check; the known one is still a library.
        Assert.Equal(50, bucket.Browsers.Counts["Unclassified"]);
        Assert.Equal(2, bucket.BotClasses[(int)BotClass.Library]);
        Assert.Empty(bucket.IgnoredRobots.Counts);
    }

    [Fact]
    public void ARobotsPathIsCutAtTheKeyCapSoAHugePathCostsNoMoreThanAShortOne()
    {
        var rig = new Rig(WithRobots(new HostRobotsSpec { Mode = RobotsMode.Custom, CustomText = "User-agent: *\nDisallow: /*beyond\n" }));
        rig.Offer(rig.Entry(path: "/" + new string('a', 3_000) + "beyond", userAgent: "SemrushBot/7"));
        rig.Offer(rig.Entry(path: "/within", userAgent: "SemrushBot/7"));

        var ignored = Ignored(rig);

        // Only what the analytics keep of a path is judged: "beyond" past the cap is not seen, "/within" is not a match either.
        Assert.Empty(ignored);
    }

    [Fact]
    public void ADroppedConnectionIsCountedAsNoneNotAsZero()
    {
        var rig = new Rig();
        rig.Offer(rig.Entry(status: 0));

        var bucket = Assert.Single(rig.Analytics.Live(), static live => live.Subject == AnalyticsSubject.Host).Bucket!;

        Assert.Equal(1, bucket.Statuses.Counts["none"]);
        Assert.DoesNotContain("0", bucket.Statuses.Counts.Keys);
        Assert.Equal(1, bucket.StatusClasses[0]);
    }

    [Fact]
    public void WhatIsKeptIsBoundedWhateverTheTrafficIs()
    {
        var rig = new Rig();
        for (var i = 0; i < 30_000; i++)
        {
            rig.Offer(rig.Entry(path: "/" + i, userAgent: "agent-" + i, client: "10." + (i / 65536 % 256) + "." + (i / 256 % 256) + "." + (i % 256)));
        }

        var bucket = Assert.Single(rig.Analytics.Live(), static live => live.Subject == AnalyticsSubject.Host).Bucket!;
        var size = TrafficBucketCodec.Encode(bucket).Data.Length;

        Assert.True(bucket.Paths.Size <= 64 && bucket.UserAgents.Size <= 32);
        Assert.True(size < 24 * 1024, $"{size} bytes for 30 000 distinct paths and agents");
        Assert.InRange(bucket.Visitors.Estimate(), 28_000, 32_000);
    }

    [Fact]
    public void ARestartInsideABucketCanBeWrittenTwiceAndBothHalvesAreWholeRollups()
    {
        var rig = new Rig();
        rig.Run(TimeSpan.FromSeconds(60));
        rig.Offer(rig.Entry());
        rig.Analytics.Flush();
        var second = new Rig();
        second.Clock.Advance(TimeSpan.FromSeconds(90));
        second.Offer(second.Entry());
        second.Run(TimeSpan.FromSeconds(1));
        second.Analytics.Flush();

        var one = Assert.Single(rig.Closed(), static rollup => rollup.Subject == AnalyticsSubject.Host);
        var two = Assert.Single(second.Closed(), static rollup => rollup.Subject == AnalyticsSubject.Host);

        Assert.Equal(one.Start, two.Start);
        Assert.True(one.CoveredSeconds + two.CoveredSeconds < 300);
    }
}
