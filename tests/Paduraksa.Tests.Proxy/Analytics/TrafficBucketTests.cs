using Paduraksa.Proxy.Analytics;
using Paduraksa.Proxy.Logging;

namespace Paduraksa.Tests.Proxy.Analytics;

/// <summary>A bucket's counts, what is and is not kept, the merge of two, and the stored form.</summary>
public sealed class TrafficBucketTests
{
    private static AccessLogEntry Entry(string path = "/", int status = 200, string method = "GET", string protocol = "HTTP/1.1", long bytes = 1_000, string? referer = null, string? userAgent = "curl/8.5.0", string host = "app.test", long totalMs = 5) => new()
    {
        Host = host,
        Path = path,
        Method = method,
        Protocol = protocol,
        StatusCode = status,
        ResponseBytes = bytes,
        Referer = referer,
        UserAgent = userAgent,
        TotalTicks = totalMs * TimeSpan.TicksPerMillisecond,
    };

    private static void Record(TrafficBucket bucket, in AccessLogEntry entry, ulong visitor = 7, bool ignored = false) =>
        bucket.Record(in entry, visitor, UserAgentClassifier.Classify(entry.UserAgent), ignored);

    [Fact]
    public void ItCountsRequestsBytesStatusesMethodsAndProtocols()
    {
        TrafficBucket bucket = new();
        Record(bucket, Entry("/a", 200, "GET", "HTTP/2", bytes: 100));
        Record(bucket, Entry("/b", 404, "POST", "HTTP/1.1", bytes: -1));
        Record(bucket, Entry("/c", 503, "GET", "HTTP/3", bytes: 50));
        Record(bucket, Entry("/d", 0, "GET", "HTTP/1.1", bytes: -1));

        Assert.Equal(4, bucket.Requests);
        Assert.Equal(150, bucket.BytesOut);
        Assert.Equal(1, bucket.ServerErrors);
        Assert.Equal([1L, 0, 1, 0, 1, 1], bucket.StatusClasses);
        Assert.Equal(1, bucket.Statuses.Counts["404"]);
        Assert.Equal(1, bucket.Statuses.Counts["none"]);
        Assert.Equal(3, bucket.Methods.Counts["GET"]);
        Assert.Equal([0L, 0, 2, 1, 1], bucket.Protocols);
    }

    [Fact]
    public void ThePathsAreCountedWithoutTheirQueryBecauseTheEntryNeverCarriesItIntoThem()
    {
        TrafficBucket bucket = new();
        var entry = Entry("/search") with { Query = "?q=secret&token=abc" };
        Record(bucket, entry);
        Record(bucket, entry);

        var top = bucket.Paths.Top(5);

        Assert.Equal(("/search", 2L), (top[0].Key, top[0].Count));
        Assert.DoesNotContain(bucket.Paths.Top(100), hitter => hitter.Key.Contains('?', StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "app.test", "direct")]
    [InlineData("", "app.test", "direct")]
    [InlineData("https://www.google.com/search?q=private+thing", "app.test", "https://www.google.com")]
    [InlineData("HTTPS://News.Example.ORG:8443/story/1#frag", "app.test", "https://news.example.org")]
    [InlineData("https://user:pass@example.org/x", "app.test", "https://example.org")]
    [InlineData("https://app.test/other-page", "app.test", "(same site)")]
    [InlineData("https://APP.test:8443/other-page", "app.test:8443", "(same site)")]
    [InlineData("android-app://com.google.android.googlequicksearchbox/", "app.test", "android-app://com.google.android.googlequicksearchbox")]
    [InlineData("https://[2001:db8::1]:8080/x", "app.test", "https://[2001:db8::1]")]
    [InlineData("not a url", "app.test", "(other)")]
    [InlineData("about:blank", "app.test", "(other)")]
    [InlineData("https:///nohost", "app.test", "(other)")]
    public void AnRefererIsReducedToItsSchemeAndHost(string? referer, string host, string expected)
    {
        Assert.Equal(expected, TrafficBucket.RefererLabel(referer, host));
    }

    [Fact]
    public void ARefererNeverCarriesAPathQueryOrCredentialsIntoTheTable()
    {
        TrafficBucket bucket = new();
        Record(bucket, Entry(referer: "https://tracker.example/pixel?uid=12345&email=a@b.test"));
        Record(bucket, Entry(referer: "https://u:p@tracker.example/other"));

        var only = Assert.Single(bucket.Referers.Top(10));

        Assert.Equal(("https://tracker.example", 2L), (only.Key, only.Count));
    }

    [Fact]
    public void ClientsAreSplitIntoBrowsersSystemsDevicesAndBots()
    {
        TrafficBucket bucket = new();
        Record(bucket, Entry(userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"));
        Record(bucket, Entry(userAgent: "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1"));
        Record(bucket, Entry(userAgent: "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)"));
        Record(bucket, Entry(userAgent: "curl/8.5.0"));
        Record(bucket, Entry(userAgent: null));

        Assert.Equal(1, bucket.Browsers.Counts["Chrome 131"]);
        Assert.Equal(1, bucket.Browsers.Counts["Safari 17"]);
        Assert.Equal(1, bucket.Browsers.Counts["Unknown"]);
        Assert.Equal(1, bucket.OperatingSystems.Counts["Windows"]);
        Assert.Equal(1, bucket.OperatingSystems.Counts["iOS"]);
        // Bots are counted as bots, not as browsers or systems.
        Assert.Equal(3, bucket.Browsers.Total);
        Assert.Equal(2, bucket.Devices[(int)DeviceClass.Bot]);
        Assert.Equal(1, bucket.Devices[(int)DeviceClass.Desktop]);
        Assert.Equal(1, bucket.Devices[(int)DeviceClass.Mobile]);
        Assert.Equal(1, bucket.BotClasses[(int)BotClass.SearchEngine]);
        Assert.Equal(1, bucket.BotClasses[(int)BotClass.Library]);
        Assert.Equal(3, bucket.BotClasses[(int)BotClass.None]);
        Assert.Equal(1, bucket.BotNames.Counts["SearchEngine|Googlebot"]);
    }

    [Fact]
    public void OnlyTheBotsTheCallerFlaggedCountAsIgnoringRobots()
    {
        TrafficBucket bucket = new();
        Record(bucket, Entry(userAgent: "GPTBot/1.2"), ignored: true);
        Record(bucket, Entry(userAgent: "GPTBot/1.2"), ignored: true);
        Record(bucket, Entry(userAgent: "Googlebot/2.1"), ignored: false);

        Assert.Equal(2, bucket.IgnoredRobots.Counts["AiCrawler|GPTBot"]);
        Assert.DoesNotContain("SearchEngine|Googlebot", bucket.IgnoredRobots.Counts.Keys);
    }

    [Fact]
    public void TheLargestResponsesAreRankedByBytesAndNotByRequests()
    {
        TrafficBucket bucket = new();
        for (var i = 0; i < 50; i++)
        {
            Record(bucket, Entry("/small", bytes: 1_000));
        }

        Record(bucket, Entry("/iso", bytes: 4_000_000_000));

        Assert.Equal("/small", bucket.Paths.Top(1)[0].Key);
        Assert.Equal("/iso", bucket.Largest.Top(1)[0].Key);
    }

    [Fact]
    public void ItsMemoryIsBoundedWhateverArrives()
    {
        TrafficBucket bucket = new();
        for (var i = 0; i < 20_000; i++)
        {
            Record(bucket, Entry("/p/" + i, 200 + (i % 400), "M" + i, referer: "https://r" + i + ".example/", userAgent: "agent/" + i + " Chrome/" + (i % 200) + ".0", bytes: i + 1), visitor: (ulong)i + 1);
        }

        Assert.True(bucket.Paths.Size <= bucket.Paths.Capacity);
        Assert.True(bucket.Referers.Size <= bucket.Referers.Capacity);
        Assert.True(bucket.UserAgents.Size <= bucket.UserAgents.Capacity);
        Assert.True(bucket.Largest.Size <= bucket.Largest.Capacity);
        Assert.True(bucket.Statuses.Counts.Count <= TrafficBucket.StatusCapacity);
        Assert.True(bucket.Methods.Counts.Count <= TrafficBucket.MethodCapacity);
        Assert.Equal(20_000, bucket.Statuses.Total);
        Assert.Equal(20_000, bucket.Methods.Total);
        Assert.True(bucket.Browsers.Counts.Count <= bucket.Browsers.Capacity);
    }

    [Fact]
    public void AnOverlongOrControlLadenKeyIsCutAndCleaned()
    {
        TrafficBucket bucket = new();
        Record(bucket, Entry("/" + new string('a', 5_000) + "\r\nInjected", userAgent: null));
        Record(bucket, Entry("/x\u0001y\u2028z"));

        var keys = bucket.Paths.Top(5).Select(static hitter => hitter.Key).ToList();

        Assert.All(keys, key => Assert.True(key.Length <= TrafficBucket.MaxKeyLength));
        Assert.All(keys, key => Assert.DoesNotContain(key, static c => char.IsControl(c) || c == '\u2028'));
        Assert.Contains("/x?y?z", keys);
        Assert.EndsWith("...", keys.First(static key => key.StartsWith("/aaa", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public void AMergeAddsEverythingAndKeepsTheVisitorsAsAUnion()
    {
        TrafficBucket a = new();
        TrafficBucket b = new();
        for (ulong i = 1; i <= 300; i++)
        {
            Record(a, Entry("/a"), visitor: HyperLogLogTests.Mix(i));
        }

        for (ulong i = 201; i <= 500; i++)
        {
            Record(b, Entry("/b", 500), visitor: HyperLogLogTests.Mix(i));
        }

        TrafficBucket total = new(scale: 4);
        total.MergeFrom(a);
        total.MergeFrom(b);

        Assert.Equal(600, total.Requests);
        Assert.Equal(300, total.ServerErrors);
        Assert.InRange(total.Visitors.Estimate(), 490, 510);
        Assert.Equal(2, total.Paths.Size);
    }

    [Fact]
    public void ACloneIsIndependent()
    {
        TrafficBucket bucket = new();
        Record(bucket, Entry("/a"));
        var clone = bucket.Clone();

        Record(bucket, Entry("/a"));

        Assert.Equal(1, clone.Requests);
        Assert.Equal(2, bucket.Requests);
    }

    [Fact]
    public void ABucketSurvivesItsStoredFormWithEveryTable()
    {
        TrafficBucket bucket = new();
        for (var i = 0; i < 500; i++)
        {
            Record(
                bucket,
                Entry("/p/" + (i % 90), i % 7 == 0 ? 502 : 200, i % 5 == 0 ? "POST" : "GET", i % 3 == 0 ? "HTTP/2" : "HTTP/1.1", bytes: (i * 37) + 1, referer: i % 4 == 0 ? "https://ref.example/x" : null, userAgent: i % 3 == 0 ? "GPTBot/1.2" : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/131.0.0.0 Safari/537.36", totalMs: 1 + (i % 40)),
                visitor: HyperLogLogTests.Mix((ulong)(i % 120)) | 1,
                ignored: i % 3 == 0);
        }

        var encoded = TrafficBucketCodec.Encode(bucket);
        var read = TrafficBucketCodec.Decode(encoded);

        Assert.NotNull(read);
        Assert.Equal((bucket.Requests, bucket.BytesOut, bucket.ServerErrors), (read.Requests, read.BytesOut, read.ServerErrors));
        Assert.Equal(bucket.StatusClasses, read.StatusClasses);
        Assert.Equal(bucket.Protocols, read.Protocols);
        Assert.Equal(bucket.Devices, read.Devices);
        Assert.Equal(bucket.BotClasses, read.BotClasses);
        Assert.Equal(bucket.Statuses.Ranked(), read.Statuses.Ranked());
        Assert.Equal(bucket.Browsers.Ranked(), read.Browsers.Ranked());
        Assert.Equal(bucket.IgnoredRobots.Ranked(), read.IgnoredRobots.Ranked());
        Assert.Equal(bucket.Visitors.Estimate(), read.Visitors.Estimate());
        Assert.Equal(bucket.Paths.Top(100).Select(static h => (h.Key, h.Count, h.Error, h.Requests, h.P95Micros)), read.Paths.Top(100).Select(static h => (h.Key, h.Count, h.Error, h.Requests, h.P95Micros)));
        Assert.Equal(bucket.Largest.Top(100).Select(static h => (h.Key, h.Count)), read.Largest.Top(100).Select(static h => (h.Key, h.Count)));
        Assert.Equal(bucket.Referers.Top(100).Select(static h => (h.Key, h.Count)), read.Referers.Top(100).Select(static h => (h.Key, h.Count)));
        Assert.Equal(bucket.UserAgents.Top(100).Select(static h => (h.Key, h.Count)), read.UserAgents.Top(100).Select(static h => (h.Key, h.Count)));
        Assert.Equal(bucket.Visitors.Estimate(), TrafficBucketCodec.DecodeVisitors(encoded.Visitors)!.Estimate());
    }

    [Fact]
    public void AFullBucketIsStoredInAFewKibibytes()
    {
        TrafficBucket bucket = new();
        for (var i = 0; i < 5_000; i++)
        {
            Record(bucket, Entry("/page/" + (i % 500), 200, referer: "https://r" + (i % 40) + ".example/", userAgent: "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/" + (100 + (i % 40)) + ".0.0.0 Safari/537.36", bytes: 2_000 + i), visitor: HyperLogLogTests.Mix((ulong)i));
        }

        var encoded = TrafficBucketCodec.Encode(bucket);

        Assert.True(encoded.Data.Length < 16 * 1024, $"{encoded.Data.Length} bytes of data");
        Assert.True(encoded.Visitors.Length <= 1 + HyperLogLog.RegisterCount);
    }

    [Fact]
    public void ACorruptOrForeignBlobIsRefusedNeverThrown()
    {
        TrafficBucket bucket = new();
        Record(bucket, Entry("/a"));
        var encoded = TrafficBucketCodec.Encode(bucket);

        Assert.Null(TrafficBucketCodec.Decode(new EncodedTrafficBucket(encoded.Visitors, [])));
        Assert.Null(TrafficBucketCodec.Decode(new EncodedTrafficBucket(encoded.Visitors, [9, 1, 2])));
        Assert.Null(TrafficBucketCodec.Decode(new EncodedTrafficBucket(encoded.Visitors, [1, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F, 1])));
        Assert.Null(TrafficBucketCodec.Decode(new EncodedTrafficBucket([], encoded.Data)));
        for (var cut = 0; cut < encoded.Data.Length; cut++)
        {
            Assert.Null(TrafficBucketCodec.Decode(new EncodedTrafficBucket(encoded.Visitors, encoded.Data[..cut])));
        }

        var flipped = encoded.Data.ToArray();
        flipped[^1] ^= 0xFF;
        // A flipped bit may still decode to something (the format has no checksum); it must never throw.
        _ = TrafficBucketCodec.Decode(new EncodedTrafficBucket(encoded.Visitors, flipped));
    }
}
