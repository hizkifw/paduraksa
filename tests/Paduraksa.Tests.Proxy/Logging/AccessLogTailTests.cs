using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Snapshot;
using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Logging;

/// <summary>
/// The live tail's hub: a fixed-size buffer of the newest entries, a bounded channel per subscriber that drops its oldest and counts,
/// filters, and the guarantee that an entry is shown the way the file's line shows it.
/// </summary>
public sealed class AccessLogTailTests
{
    private static readonly AccessLogFormatOptions Options = AccessLogFormatOptions.Default;

    private static AccessLogEntry Entry(string path = "/", int status = 200, string? host = "app.test", string? client = "198.51.100.7", string method = "GET") => new()
    {
        TimestampUtcTicks = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc).Ticks,
        ClientAddress = client,
        Host = host,
        Method = method,
        Path = path,
        Protocol = "HTTP/1.1",
        StatusCode = status,
        RequestId = "rid-" + path,
        UpstreamAddress = "http://10.0.0.2:8080/",
        CloudflareRay = "8f2a1c3d4e5f6789",
        Referer = "https://example.test/",
        UserAgent = "agent/1.0",
    };

    private static SnapshotStore StoreWith(params string[] hostNames)
    {
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:9/")],
            Hosts = [.. hostNames.Select((name, i) => ProxyHost("h" + i, name, Route("r" + i, "web")))],
        };
        SnapshotStore store = new();
        store.Publish(Bound(model));
        return store;
    }

    [Fact]
    public void TheBufferKeepsTheNewestEntriesOldestFirstWithContiguousSequences()
    {
        AccessLogTail tail = new(8, Options);
        for (var i = 1; i <= 20; i++)
        {
            tail.Offer(Entry("/" + i));
        }

        var kept = tail.Recent(AccessLogTailFilter.All, 100);

        Assert.Equal(8, kept.Count);
        Assert.Equal(Enumerable.Range(13, 8).Select(i => "/" + i), kept.Select(e => e.Path));
        Assert.Equal(Enumerable.Range(13, 8).Select(i => (long)i), kept.Select(e => e.Sequence));
        Assert.Equal(20, tail.Offered);
        Assert.Equal((string[])["/19", "/20"], tail.Recent(AccessLogTailFilter.All, 2).Select(e => e.Path));
    }

    [Fact]
    public void AnEmptyBufferHasNothingToShow()
    {
        AccessLogTail tail = new(8, Options);

        Assert.Empty(tail.Recent(AccessLogTailFilter.All, 10));
        using var subscription = tail.Subscribe(AccessLogTailFilter.All, 10)!;
        Assert.Empty(subscription.Backlog);
    }

    [Fact]
    public void ASubscriberGetsTheBacklogThenEveryLaterEntryOnceWithNoGap()
    {
        AccessLogTail tail = new(16, Options);
        tail.Offer(Entry("/1"));
        tail.Offer(Entry("/2"));
        tail.Offer(Entry("/3"));
        using var subscription = tail.Subscribe(AccessLogTailFilter.All, 2)!;
        tail.Offer(Entry("/4"));
        tail.Offer(Entry("/5"));

        Assert.Equal((string[])["/2", "/3"], subscription.Backlog.Select(e => e.Path));
        List<string?> streamed = [];
        while (subscription.Reader.TryRead(out var entry))
        {
            streamed.Add(entry.Path);
        }

        Assert.Equal((string[])["/4", "/5"], streamed);
        Assert.Equal(0, subscription.Skipped);
    }

    [Fact]
    public void ASlowSubscriberLosesItsOldestEntriesAndCountsThemWhileTheBufferIsUntouched()
    {
        AccessLogTail tail = new(64, Options);
        using var subscription = tail.Subscribe(AccessLogTailFilter.All, 0, channelCapacity: 4)!;
        for (var i = 1; i <= 10; i++)
        {
            tail.Offer(Entry("/" + i));
        }

        List<long> streamed = [];
        while (subscription.Reader.TryRead(out var entry))
        {
            streamed.Add(entry.Sequence);
        }

        Assert.Equal((long[])[7, 8, 9, 10], streamed);
        Assert.Equal(6, subscription.Skipped);
        Assert.Equal(6, subscription.TakeSkipped());
        Assert.Equal(0, subscription.TakeSkipped());
        Assert.Equal(10, tail.Recent(AccessLogTailFilter.All, 100).Count);
    }

    [Fact]
    public void AFilterKeepsALineOutOfTheChannelSoItNeverCountsAsSkipped()
    {
        AccessLogTail tail = new(64, Options);
        using var subscription = tail.Subscribe(new AccessLogTailFilter { StatusClass = 5 }, 0, channelCapacity: 2)!;
        for (var i = 0; i < 50; i++)
        {
            tail.Offer(Entry("/ok", 200));
        }

        tail.Offer(Entry("/bad", 502));

        Assert.True(subscription.Reader.TryRead(out var entry));
        Assert.Equal("/bad", entry.Path);
        Assert.Equal(0, subscription.Skipped);
    }

    [Fact]
    public void SubscribersAreBoundedAndADisposedOneFreesItsPlace()
    {
        AccessLogTail tail = new(8, Options);
        var subscriptions = Enumerable.Range(0, AccessLogTail.MaxSubscribers)
            .Select(_ => tail.Subscribe(AccessLogTailFilter.All, 0)!)
            .ToList();

        Assert.Null(tail.Subscribe(AccessLogTailFilter.All, 0));
        subscriptions[3].Dispose();
        subscriptions[3].Dispose();

        Assert.Equal(AccessLogTail.MaxSubscribers - 1, tail.SubscriberCount);
        using var again = tail.Subscribe(AccessLogTailFilter.All, 0);
        Assert.NotNull(again);
        subscriptions.ForEach(s => s.Dispose());
    }

    [Fact]
    public async Task DisposingCompletesTheReader()
    {
        AccessLogTail tail = new(8, Options);
        var subscription = tail.Subscribe(AccessLogTailFilter.All, 0)!;

        subscription.Dispose();

        Assert.False(await subscription.Reader.WaitToReadAsync(TestContext.Current.CancellationToken));
        tail.Offer(Entry());
        Assert.Equal(0, tail.SubscriberCount);
    }

    public static TheoryData<AccessLogTailFilter, string[]> Filters() => new()
    {
        { AccessLogTailFilter.All, ["/a", "/b", "/c", "/d", "/e"] },
        { new() { StatusClass = 4 }, ["/b", "/d"] },
        { new() { StatusClass = 2 }, ["/a", "/c", "/e"] },
        { new() { Status = 404 }, ["/b"] },
        { new() { Status = 404, StatusClass = 2 }, ["/b"] },
        { new() { ClientAddressPrefix = "10.0." }, ["/c"] },
        { new() { ClientAddressPrefix = "2001:DB8:" }, ["/e"] },
        { new() { Method = "post" }, ["/c"] },
        { new() { PathContains = "A" }, ["/a"] },
        { new() { Host = "APP.test" }, ["/a", "/b", "/c", "/d"] },
        { new() { Host = "other.test" }, [] },
        { new() { UnmatchedHost = true }, ["/e"] },
        { new() { Host = "app.test", StatusClass = 4, Method = "GET" }, ["/b", "/d"] },
    };

    [Theory]
    [MemberData(nameof(Filters))]
    public void FiltersKeepTheMatchingEntries(AccessLogTailFilter filter, string[] expected)
    {
        AccessLogTail tail = new(16, Options, StoreWith("app.test"));
        tail.Offer(Entry("/a"));
        tail.Offer(Entry("/b", 404));
        tail.Offer(Entry("/c", 200, client: "10.0.0.9", method: "POST"));
        tail.Offer(Entry("/d", 403));
        tail.Offer(Entry("/e", 200, host: "nobody.test", client: "2001:db8::1"));

        Assert.Equal(expected, tail.Recent(filter, 100).Select(e => e.Path));
        using var subscription = tail.Subscribe(filter, 100)!;
        Assert.Equal(expected, subscription.Backlog.Select(e => e.Path));
    }

    [Fact]
    public void AStatusClassNeverMatchesARequestTheGateDroppedWithoutAnAnswer()
    {
        AccessLogTail tail = new(8, Options);
        tail.Offer(Entry("/dropped", AccessLogEntry.NoResponse));

        Assert.Empty(tail.Recent(new() { StatusClass = 2 }, 10));
        Assert.Single(tail.Recent(AccessLogTailFilter.All, 10));
    }

    [Fact]
    public void AWildcardHostOwnsItsRequestsAndAnyOfAHostsNamesFindsThem()
    {
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:9/")],
            Hosts =
            [
                ProxyHost("wild", "*.wild.test", Route("r1", "web")),
                ProxyHost("two", "one.test", Route("r2", "web")) with { Names = ["one.test", "two.test"] },
            ],
        };
        SnapshotStore store = new();
        store.Publish(Bound(model));
        AccessLogTail tail = new(8, Options, store);
        tail.Offer(Entry("/w", host: "a.wild.test:8443"));
        tail.Offer(Entry("/t", host: "TWO.test"));
        tail.Offer(Entry("/n", host: "wild.test"));

        Assert.Equal((string[])["/w"], tail.Recent(new() { Host = "*.wild.test" }, 10).Select(e => e.Path));
        Assert.Equal((string[])["/t"], tail.Recent(new() { Host = "one.test" }, 10).Select(e => e.Path));
        Assert.Equal((string[])["/n"], tail.Recent(new() { UnmatchedHost = true }, 10).Select(e => e.Path));
    }

    [Fact]
    public void ACopyIsNotTheRingsSlot()
    {
        // The ring reuses a slot; an offered entry is a copy, so changing what the producer holds changes nothing in the tail.
        AccessLogTail tail = new(4, Options);
        var entry = Entry("/first");
        tail.Offer(entry);
        entry = entry with { Path = "/second" };

        Assert.Equal("/first", tail.Recent(AccessLogTailFilter.All, 1)[0].Path);
    }

    [Fact]
    public async Task OfferingWhileSubscribingNeverLosesOrRepeatsAnEntryBetweenTheBacklogAndTheStream()
    {
        const int Entries = 60_000;
        AccessLogTail tail = new(Entries, Options);
        var producer = Task.Run(
            () =>
            {
                for (var i = 0; i < Entries; i++)
                {
                    tail.Offer(Entry("/" + i));
                }
            },
            TestContext.Current.CancellationToken);

        List<AccessLogTailSubscription> subscriptions = [];
        while (!producer.IsCompleted && subscriptions.Count < AccessLogTail.MaxSubscribers)
        {
            subscriptions.Add(tail.Subscribe(AccessLogTailFilter.All, 50, channelCapacity: Entries)!);
            Thread.SpinWait(20_000);
        }

        await producer;
        foreach (var subscription in subscriptions)
        {
            List<long> sequences = [.. subscription.Backlog.Select(e => e.Sequence)];
            while (subscription.Reader.TryRead(out var entry))
            {
                sequences.Add(entry.Sequence);
            }

            Assert.Equal(Entries, sequences[^1]);
            for (var i = 1; i < sequences.Count; i++)
            {
                Assert.Equal(sequences[i - 1] + 1, sequences[i]);
            }

            subscription.Dispose();
        }
    }

    [Theory]
    [InlineData("/plain")]
    [InlineData("/a b/\u00fcn\u00ef")]
    [InlineData("/line\r\nbreak\t\u007f")]
    [InlineData("/quote\"and\\backslash")]
    [InlineData("/nel\x85_next")]
    [InlineData("/separators\x2028_and\x2029_paragraph")]
    [InlineData("/all\"\\\x85\x2028\r_together")]
    public void AnEntryShowsTheFieldsTheFileLineShows(string path)
    {
        const string Hostile = "a\"b\\c\r\nd\x85_e\x2028_f\x2029_g\x9f_h\x7f_i";
        var entry = Entry(path) with
        {
            Host = "host" + Hostile,
            Query = "?q=1&token=hunter2&Pwd&x=" + Hostile + "&y=\"z",
            UserAgent = "agent/1.0" + Hostile,
            Referer = "https://example.test/" + Hostile,
            UpstreamAddress = "http://10.0.0.2:8080/" + Hostile,
            CloudflareRay = "ray" + Hostile,
            RequestId = "rid" + Hostile,
        };
        AccessLogTail tail = new(4, Options);
        tail.Offer(entry);
        var shown = tail.Recent(AccessLogTailFilter.All, 1)[0];

        var file = FileFields(entry, Options);

        // The file's quoted text, escapes and all: nothing is unescaped here, so a character one side leaves raw and the other escapes differs.
        Assert.Equal(file.Target, $"{shown.Method} {shown.Path}{shown.Query} {shown.Protocol}");
        Assert.Equal(file.Host, shown.Host);
        Assert.Equal(file.Upstream, shown.UpstreamAddress);
        Assert.Equal(file.RequestId, shown.RequestId);
        Assert.Equal(file.CloudflareRay, shown.CloudflareRay);
        Assert.Equal(file.Referer, shown.Referer);
        Assert.Equal(file.UserAgent, shown.UserAgent);
        Assert.DoesNotContain("hunter2", shown.Query, StringComparison.Ordinal);
        Assert.Contains("token=REDACTED", shown.Query, StringComparison.Ordinal);
        Assert.Contains("Pwd=REDACTED", shown.Query, StringComparison.Ordinal);
        string[] shownText = [shown.Path!, shown.Query!, shown.Host!, shown.UserAgent!, shown.Referer!, shown.RequestId!, shown.UpstreamAddress!, shown.CloudflareRay!];
        foreach (var text in shownText)
        {
            Assert.DoesNotContain(text, c => c < ' ' || c is >= '\x7F' and <= '\x9F' || c is '\x2028' or '\x2029');
        }
    }

    [Fact]
    public void ALeftOutQueryStringIsLeftOutOfTheTailToo()
    {
        AccessLogFormatOptions options = new() { LogQueryString = false };
        AccessLogTail tail = new(4, options);
        var entry = Entry("/search") with { Query = "?q=secret-thing" };
        tail.Offer(entry);

        var shown = tail.Recent(AccessLogTailFilter.All, 1)[0];

        Assert.Null(shown.Query);
        Assert.Equal("GET /search HTTP/1.1", FileFields(entry, options).Target);
    }

    [Fact]
    public void AReplacedSensitiveListIsHonouredByTheTail()
    {
        AccessLogFormatOptions options = new() { SensitiveQueryKeys = ["pin"] };
        AccessLogTail tail = new(4, options);
        tail.Offer(Entry("/x") with { Query = "?pin=1234&token=visible" });

        Assert.Equal("?pin=REDACTED&token=visible", tail.Recent(AccessLogTailFilter.All, 1)[0].Query);
    }

    [Fact]
    public void LongFieldsAreCutAtTheFilesCapsAndEndWithTheMarker()
    {
        AccessLogTail tail = new(4, Options);
        tail.Offer(Entry(new string('p', 5_000)) with { Query = "?q=" + new string('q', 5_000), UserAgent = new string('u', 5_000), Host = new string('h', 400) });

        var shown = tail.Recent(AccessLogTailFilter.All, 1)[0];

        Assert.Equal(AccessLogFormatter.MaxPathLength + 3, shown.Path!.Length);
        Assert.EndsWith(AccessLogFormatter.TruncationMarker, shown.Path, StringComparison.Ordinal);
        Assert.Equal(AccessLogFormatter.MaxHeaderValueLength + 3, shown.UserAgent!.Length);
        Assert.Equal(AccessLogFormatter.MaxHostLength + 3, shown.Host!.Length);
        Assert.InRange(shown.Query!.Length, 1, AccessLogFormatter.MaxQueryLength + 3);
        Assert.EndsWith(AccessLogFormatter.TruncationMarker, shown.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatTheBufferKeepsOfALongFieldIsBoundedBeforeAnyoneReadsIt()
    {
        var huge = new string('u', 1_000_000);
        var kept = AccessLogTailEntry.Bound(Entry("/x") with { UserAgent = huge, Referer = huge, Path = huge, Query = huge, Host = huge });

        Assert.Equal(AccessLogFormatter.MaxHeaderValueLength + 1, kept.UserAgent!.Length);
        Assert.Equal(AccessLogFormatter.MaxHeaderValueLength + 1, kept.Referer!.Length);
        Assert.Equal(AccessLogFormatter.MaxPathLength + 1, kept.Path!.Length);
        Assert.Equal(AccessLogFormatter.MaxHostLength + 1, kept.Host!.Length);
        Assert.Equal(AccessLogFormatter.MaxQueryLength + 1, kept.Query!.Length);
    }

    /// <summary>
    /// The stored query is cut at the file's cap before it is redacted. A sensitive parameter that straddles the cut must still show
    /// no value: its name with a partial value reads REDACTED, a partial name has no value to show. An earlier long sensitive value
    /// shrinks under redaction, so the result still fits the visible cap and the cut is what the marker reports.
    /// </summary>
    [Theory]
    [InlineData(402, "token=REDACTED")]
    [InlineData(405, "token=REDACTED")]
    [InlineData(408, "&tok")]
    public void ASensitiveParameterAtTheCutOfTheStoredQueryShowsNoValue(int filler, string expectedEnd)
    {
        var query = "?password=" + new string('p', 600) + "&a=" + new string('x', filler) + "&token=" + string.Concat(Enumerable.Repeat("SECRET", 20));
        Assert.True(query.Length > AccessLogFormatter.MaxQueryLength + 1);
        AccessLogTail tail = new(4, Options);
        tail.Offer(Entry("/x") with { Query = query });

        var shown = tail.Recent(AccessLogTailFilter.All, 1)[0].Query!;

        Assert.DoesNotContain("SEC", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("ppp", shown, StringComparison.Ordinal);
        Assert.StartsWith("?password=REDACTED&a=x", shown, StringComparison.Ordinal);
        Assert.EndsWith(expectedEnd + AccessLogFormatter.TruncationMarker, shown, StringComparison.Ordinal);
    }

    [Fact]
    public void AFieldThatNeedsNoChangeKeepsTheStringTheRequestHeld()
    {
        AccessLogTail tail = new(4, Options);
        var entry = Entry("/same");
        tail.Offer(entry);

        Assert.Same(entry.Path, tail.Recent(AccessLogTailFilter.All, 1)[0].Path);
    }

    [Fact]
    public void TheDrainOffersEachEntryToTheTailAfterTheFileAndAFailingSinkDoesNotHideIt()
    {
        AccessLogRing ring = new(16);
        AccessLogTail tail = new(8, Options);
        ThrowingSink sink = new();
        AccessLogDrainService drain = new(ring, sink, Options, NullLogger<AccessLogDrainService>.Instance, tail);
        Assert.True(ring.TryWrite(Entry("/one")));
        Assert.True(ring.TryWrite(Entry("/two")));

        drain.Drain();

        Assert.Equal(2, sink.Attempts);
        Assert.Equal((string[])["/one", "/two"], tail.Recent(AccessLogTailFilter.All, 10).Select(e => e.Path));
        drain.Dispose();
    }

    /// <summary>The quoted fields of the file's line for <paramref name="entry"/>, exactly as written between their quotes (escapes included).</summary>
    private static (string Host, string Target, string Upstream, string RequestId, string CloudflareRay, string Referer, string UserAgent) FileFields(AccessLogEntry entry, AccessLogFormatOptions options)
    {
        var buffer = new char[AccessLogFormatter.MaxLineLength];
        var length = AccessLogFormatter.Format(in entry, options, buffer);
        var quoted = Quoted(new string(buffer, 0, length));
        Assert.Equal(7, quoted.Count);
        return (quoted[0], quoted[1], quoted[2], quoted[3], quoted[4], quoted[5], quoted[6]);
    }

    /// <summary>The text between each pair of quotes, taken as written: a backslash keeps the character after it inside the field, nothing is unescaped.</summary>
    private static List<string> Quoted(string line)
    {
        List<string> fields = [];
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] != '"')
            {
                continue;
            }

            var start = ++i;
            while (line[i] != '"')
            {
                i += line[i] == '\\' ? 2 : 1;
            }

            fields.Add(line[start..i]);
        }

        return fields;
    }

    private sealed class ThrowingSink : IAccessLogSink
    {
        public int Attempts { get; private set; }

        public void WriteLine(ReadOnlySpan<char> line)
        {
            Attempts++;
            throw new IOException("disk full");
        }

        public void Flush()
        {
        }
    }
}
