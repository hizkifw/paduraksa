using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Paduraksa.Application.Cloudflare;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.ProxyProtocol;

namespace Paduraksa.Tests.Unit.Cloudflare;

/// <summary>The edge-range refresh job against a fake Cloudflare: what it accepts, what it keeps, and when it asks for a publish.</summary>
public sealed class CloudflareRangeRefresherTests : IDisposable
{
    private const string Api = "https://api.cloudflare.test/client/v4/ips";
    private const string V4 = "https://www.cloudflare.test/ips-v4";
    private const string V6 = "https://www.cloudflare.test/ips-v6";

    private static readonly string[] ListV4 = ["173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "104.16.0.0/13"];
    private static readonly string[] ListV6 = ["2400:cb00::/32", "2606:4700::/32", "2803:f800::/32"];

    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "paduraksa-cf-" + Path.GetRandomFileName());
    private readonly FakeCloudflare _cloudflare = new();
    private readonly CloudflareEdgeRangeSource _source;
    private readonly CloudflareRangeFile _file;
    private readonly SnapshotStore _store = new();
    private readonly Clock _clock = new(Now);
    private int _published;
    private TrustedProxyMode _mode = TrustedProxyMode.CloudflareProxy;

    /// <summary>What the next republish does: true queues a publish that goes live, false is a skipped or failed one.</summary>
    private readonly Queue<bool> _republishes = new();

    public CloudflareRangeRefresherTests()
    {
        Directory.CreateDirectory(_directory);
        _source = new CloudflareEdgeRangeSource(_clock);
        _file = new CloudflareRangeFile(Path.Combine(_directory, CloudflareRangeFile.FileName), NullLogger.Instance);
        PublishLive(TrustedProxyMode.CloudflareProxy);
    }

    public void Dispose()
    {
        _store.Dispose();
        _cloudflare.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private void PublishLive(TrustedProxyMode mode)
    {
        _mode = mode;
        var result = SnapshotCompiler.Compile(
            new ProxyConfigModel
            {
                Defaults = new DefaultsSpec { TrustedProxies = new TrustedProxySpec { Mode = mode, Proxies = mode is TrustedProxyMode.Generic or TrustedProxyMode.CloudflareTunnel ? ["10.0.0.0/8"] : [] } },
            },
            new SnapshotCompilerOptions { CloudflareRanges = _source });
        _store.Publish(result.Snapshot!.Bind(_store.Current.Generation + 1, []));
    }

    private CloudflareRangeRefresher Refresher(CloudflareRangeRefreshOptions? options = null) => new(
        _source,
        _file,
        new HttpClient(_cloudflare),
        options ?? Options(),
        _store,
        () =>
        {
            _published++;
            if (_republishes.TryDequeue(out var outcome) && !outcome)
            {
                return Task.FromResult(false);
            }

            // What a real publish does: the live config is compiled again, with the list in force.
            PublishLive(_mode);
            return Task.FromResult(true);
        },
        _clock,
        NullLogger<CloudflareRangeRefresher>.Instance);

    private static CloudflareRangeRefreshOptions Options() => new()
    {
        ApiUrl = new Uri(Api),
        TextV4Url = new Uri(V4),
        TextV6Url = new Uri(V6),
        StartDelay = TimeSpan.Zero,
        Interval = TimeSpan.FromMilliseconds(20),
        Jitter = TimeSpan.Zero,
        MinRetry = TimeSpan.FromMilliseconds(5),
        MaxRetry = TimeSpan.FromMilliseconds(40),
        RequestTimeout = TimeSpan.FromSeconds(2),
    };

    private static string Json(string[] v4, string[] v6, bool success = true) =>
        $$"""{"result":{"ipv4_cidrs":[{{Quote(v4)}}],"ipv6_cidrs":[{{Quote(v6)}}],"etag":"abc"},"success":{{(success ? "true" : "false")}},"errors":[],"messages":[]}""";

    private static string Quote(string[] values) => string.Join(',', values.Select(v => $"\"{v}\""));

    private void ServeJson(string[]? v4 = null, string[]? v6 = null) => _cloudflare.Serve(Api, Json(v4 ?? ListV4, v6 ?? ListV6));

    [Fact]
    public async Task ASuccessfulJsonFetchReplacesTheListStoresItAndAsksForAPublish()
    {
        ServeJson();
        using var refresher = Refresher();

        Assert.Equal(RangeRefreshResult.Replaced, await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken));

        var current = _source.Current;
        Assert.Equal(EdgeRangeOrigin.Fetched, current.Origin);
        Assert.Equal(7, current.Count);
        Assert.Equal(Now, current.FetchedAt);
        Assert.True(current.Ranges.Contains(IpKey.Parse("104.16.9.9")));
        Assert.Equal(1, _published);

        // Stored, so a restart keeps it.
        var stored = _file.Load();
        Assert.NotNull(stored);
        Assert.True(stored.SameBlocksAs(current));
        Assert.Equal(Now, stored.FetchedAt);
        Assert.Equal([Api], _cloudflare.Requests);
    }

    [Fact]
    public async Task TheSameListAgainOnlyMovesItsAgeAndPublishesNothing()
    {
        ServeJson();
        using var refresher = Refresher();
        await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);
        _clock.Now = Now.AddDays(1);

        Assert.Equal(RangeRefreshResult.Unchanged, await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1, _published);
        Assert.Equal(Now.AddDays(1), _source.Current.FetchedAt);
        Assert.Equal(Now.AddDays(1), _file.Load()!.FetchedAt);

        // A list with one more range is a change.
        ServeJson(v4: [.. ListV4, "141.101.64.0/18"]);
        Assert.Equal(RangeRefreshResult.Replaced, await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, _published);
    }

    [Fact]
    public async Task ThePlainTextEndpointsAreTheFallbackWhenTheJsonOneFails()
    {
        _cloudflare.Serve(Api, status: HttpStatusCode.ServiceUnavailable);
        _cloudflare.Serve(V4, string.Join('\n', ListV4) + "\n");
        _cloudflare.Serve(V6, string.Join("\r\n", ListV6) + "\r\n\r\n");
        using var refresher = Refresher();

        Assert.Equal(RangeRefreshResult.Replaced, await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken));

        Assert.Equal(7, _source.Current.Count);
        Assert.Equal([Api, V4, V6], _cloudflare.Requests);
        Assert.Equal(0, _source.ConsecutiveFailures);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"success":false,"result":{"ipv4_cidrs":[],"ipv6_cidrs":[]}}""")]
    [InlineData("""{"success":true}""")]
    [InlineData("""{"success":true,"result":{"ipv4_cidrs":"173.245.48.0/20","ipv6_cidrs":[]}}""")]
    [InlineData("""{"success":true,"result":{"ipv4_cidrs":["0.0.0.0/0","173.245.48.0/20","103.21.244.0/22","103.22.200.0/22"],"ipv6_cidrs":["2400:cb00::/32","2606:4700::/32","2803:f800::/32"]}}""")]
    [InlineData("""{"success":true,"result":{"ipv4_cidrs":["173.245.48.0/20","103.21.244.0/22","103.22.200.0/22"],"ipv6_cidrs":["::/0","2606:4700::/32","2803:f800::/32"]}}""")]
    [InlineData("""{"success":true,"result":{"ipv4_cidrs":["173.245.48.0/20","103.21.244.0/22","103.22.200.0/22"],"ipv6_cidrs":[]}}""")]
    [InlineData("""{"success":true,"result":{"ipv4_cidrs":["173.245.48.0/20","103.21.244.0/22","nonsense"],"ipv6_cidrs":["2400:cb00::/32","2606:4700::/32","2803:f800::/32"]}}""")]
    [InlineData("""{"success":true,"result":{"ipv4_cidrs":["173.245.48.0/20","103.21.244.0/22",7],"ipv6_cidrs":["2400:cb00::/32","2606:4700::/32","2803:f800::/32"]}}""")]
    public async Task AMalformedOrSuspiciousListIsRefusedAndTheLastGoodOneStays(string body)
    {
        ServeJson();
        using var refresher = Refresher();
        await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);
        var good = _source.Current;
        var published = _published;

        _cloudflare.Serve(Api, body);
        _cloudflare.Serve(V4, "also not a list\n");
        _cloudflare.Serve(V6, "2400:cb00::/32\n");

        Assert.Equal(RangeRefreshResult.Failed, await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken));

        Assert.Same(good, _source.Current);
        Assert.Equal(1, _source.ConsecutiveFailures);
        Assert.NotNull(_source.LastFailure);
        Assert.True(_file.Load()!.SameBlocksAs(good), "the stored list is the good one");
        Assert.Equal(published, _published);
    }

    [Fact]
    public async Task ASuspiciousJsonListFallsThroughToTheTextEndpoints()
    {
        _cloudflare.Serve(Api, """{"success":true,"result":{"ipv4_cidrs":["0.0.0.0/0","1.0.0.0/8","2.0.0.0/8"],"ipv6_cidrs":["2400:cb00::/32","2606:4700::/32","2803:f800::/32"]}}""");
        _cloudflare.Serve(V4, string.Join('\n', ListV4));
        _cloudflare.Serve(V6, string.Join('\n', ListV6));
        using var refresher = Refresher();

        Assert.Equal(RangeRefreshResult.Replaced, await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken));

        Assert.False(_source.Current.Ranges.Contains(IpKey.Parse("1.2.3.4")));
    }

    [Fact]
    public async Task AnOversizedBodyAnErrorStatusAndATimeoutAreEachAFailure()
    {
        using var refresher = Refresher(Options() with { MaxBodyBytes = 1024, RequestTimeout = TimeSpan.FromMilliseconds(200) });

        _cloudflare.Serve(Api, new string('x', 5000));
        _cloudflare.Serve(V4, status: HttpStatusCode.InternalServerError);
        _cloudflare.Serve(V6, status: HttpStatusCode.NotFound);
        Assert.Equal(RangeRefreshResult.Failed, await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken));
        Assert.Contains("larger than 1024 bytes", _source.LastFailure, StringComparison.Ordinal);

        // A body that streams past the cap with no Content-Length is cut at the cap too.
        _cloudflare.ServeUnbounded(Api);
        Assert.Equal(RangeRefreshResult.Failed, await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken));
        Assert.Contains("larger than 1024 bytes", _source.LastFailure, StringComparison.Ordinal);

        _cloudflare.Serve(Api, delay: TimeSpan.FromSeconds(5));
        _cloudflare.Serve(V4, delay: TimeSpan.FromSeconds(5));
        Assert.Equal(RangeRefreshResult.Failed, await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken));
        Assert.Contains("timed out", _source.LastFailure, StringComparison.Ordinal);

        _cloudflare.Throw(Api, new HttpRequestException(HttpRequestError.ConnectionError, "refused"));
        _cloudflare.Throw(V4, new HttpRequestException(HttpRequestError.ConnectionError, "refused"));
        Assert.Equal(RangeRefreshResult.Failed, await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken));
        Assert.Contains("ConnectionError", _source.LastFailure, StringComparison.Ordinal);
        Assert.Equal(4, _source.ConsecutiveFailures);
        Assert.Same(EdgeRangeList.BuiltIn, _source.Current);
    }

    [Fact]
    public async Task ARefusedTextListNamesBothEndpointsAndAnUnexpectedErrorKeepsItsMessage()
    {
        _cloudflare.Serve(Api, status: HttpStatusCode.BadGateway);
        _cloudflare.Serve(V4, string.Join('\n', ListV4));
        _cloudflare.Serve(V6, "::/0\n");
        using var refresher = Refresher();

        Assert.Equal(RangeRefreshResult.Failed, await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken));
        Assert.Contains("www.cloudflare.test/ips-v4 and www.cloudflare.test/ips-v6 gave a list that was refused", _source.LastFailure, StringComparison.Ordinal);

        // Whatever the loop didn't expect is recorded with its message, not just its type.
        _cloudflare.Throw(Api, new InvalidOperationException("the handler blew up"));
        await refresher.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => _source.LastFailure?.StartsWith("InvalidOperationException: the handler blew up", StringComparison.Ordinal) == true);
        await refresher.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SeveralFailuresInARowChangeTheWarningAndPublishOnce()
    {
        _cloudflare.Serve(Api, status: HttpStatusCode.BadGateway);
        _cloudflare.Serve(V4, status: HttpStatusCode.BadGateway);
        _cloudflare.Serve(V6, status: HttpStatusCode.BadGateway);
        using var refresher = Refresher();

        // The built-in list is fresh in this test's clock, so only the failure count raises the warning, on the third.
        _clock.Now = EdgeRangeList.BuiltIn.FetchedAt.AddDays(1);
        await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);
        await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, _published);
        Assert.Null(_source.Status().Warning);

        await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, _published);
        Assert.Contains("keep failing", _source.Status().Warning, StringComparison.Ordinal);

        // The same warning again isn't a change.
        await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, _published);

        // A good refresh clears it, which is a change too.
        ServeJson();
        await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, _published);
        Assert.Null(_source.Status().Warning);
    }

    [Fact]
    public async Task AListThatAgesPastThirtyDaysPublishesItsWarningWhenTheNextRefreshSeesIt()
    {
        using var refresher = Refresher();
        _clock.Now = EdgeRangeList.BuiltIn.FetchedAt.AddDays(29);
        _cloudflare.Serve(Api, status: HttpStatusCode.BadGateway);
        _cloudflare.Serve(V4, status: HttpStatusCode.BadGateway);
        await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, _published);

        _clock.Now = EdgeRangeList.BuiltIn.FetchedAt.AddDays(31);
        await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, _published);
        Assert.Contains("31 days old", _source.Status().Warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TrustedProxyMode.Off)]
    [InlineData(TrustedProxyMode.Generic)]
    [InlineData(TrustedProxyMode.CloudflareTunnel)]
    public async Task NothingIsPublishedWhileTheLiveConfigDoesNotTrustTheEdgeRanges(TrustedProxyMode mode)
    {
        PublishLive(mode);
        ServeJson();
        using var refresher = Refresher();

        Assert.Equal(RangeRefreshResult.Replaced, await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken));

        Assert.Equal(0, _published);
        Assert.Equal(EdgeRangeOrigin.Fetched, _source.Current.Origin);
    }

    [Fact]
    public async Task APublishThatThrowsIsLoggedAndTheListStays()
    {
        ServeJson();
        using CloudflareRangeRefresher refresher = new(
            _source, _file, new HttpClient(_cloudflare), Options(), _store, () => throw new InvalidOperationException("config store busy"), _clock, NullLogger<CloudflareRangeRefresher>.Instance);

        Assert.Equal(RangeRefreshResult.Replaced, await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EdgeRangeOrigin.Fetched, _source.Current.Origin);
    }

    [Fact]
    public async Task APublishThatWasSkippedOrFailedIsTriedAgainUntilTheNewListIsLive()
    {
        ServeJson();
        using var refresher = Refresher();
        _republishes.Enqueue(false); // skipped: the store can't take commands

        Assert.Equal(RangeRefreshResult.Replaced, await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, _published);
        Assert.Same(EdgeRangeList.BuiltIn, _store.Current.EdgeRanges);

        // The list hasn't changed since, and a failing Cloudflare doesn't matter: the publish is still owed.
        _cloudflare.Serve(Api, status: HttpStatusCode.BadGateway);
        _cloudflare.Serve(V4, status: HttpStatusCode.BadGateway);
        _republishes.Enqueue(true);
        Assert.Equal(RangeRefreshResult.Failed, await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken));

        Assert.Equal(2, _published);
        Assert.Equal(EdgeRangeOrigin.Fetched, _store.Current.EdgeRanges!.Origin);
        Assert.True(_store.Current.TrustedProxies.IsTrustedPeer(IpKey.Parse("104.16.9.9")));

        // Nothing is owed any more.
        await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, _published);
    }

    [Fact]
    public async Task APublishThatThrowsIsRetriedAndTheWarningIsOnlyRecordedOnceItWentOut()
    {
        _cloudflare.Serve(Api, status: HttpStatusCode.BadGateway);
        _cloudflare.Serve(V4, status: HttpStatusCode.BadGateway);
        var throwing = true;
        using CloudflareRangeRefresher refresher = new(
            _source, _file, new HttpClient(_cloudflare), Options(), _store,
            () =>
            {
                _published++;
                return throwing ? throw new InvalidOperationException("database is locked") : Task.FromResult(true);
            },
            _clock, NullLogger<CloudflareRangeRefresher>.Instance);
        _clock.Now = EdgeRangeList.BuiltIn.FetchedAt.AddDays(31);

        await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);
        await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, _published);

        throwing = false;
        await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, _published);
        await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, _published);
    }

    [Fact]
    public async Task TheLoopRetriesAPendingPublishOnTheBackoffWithoutWaitingForTheNextFetch()
    {
        ServeJson();
        _republishes.Enqueue(false);
        _republishes.Enqueue(false);
        using var refresher = Refresher(Options() with { Interval = TimeSpan.FromHours(1) });

        await refresher.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => _store.Current.EdgeRanges?.Origin == EdgeRangeOrigin.Fetched);
        await refresher.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, _published);
        Assert.Equal(1, _cloudflare.Requests.Count(r => r == Api));
    }

    [Fact]
    public async Task AnUnwritableStoreDoesNotRejectTheList()
    {
        ServeJson();
        var blocked = new CloudflareRangeFile(Path.Combine(_directory, "missing-dir", CloudflareRangeFile.FileName), NullLogger.Instance);
        using CloudflareRangeRefresher refresher = new(
            _source, blocked, new HttpClient(_cloudflare), Options(), _store, () => Task.FromResult(true), _clock, NullLogger<CloudflareRangeRefresher>.Instance);

        Assert.Equal(RangeRefreshResult.Replaced, await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EdgeRangeOrigin.Fetched, _source.Current.Origin);
    }

    [Fact]
    public async Task TheLoopFetchesOnceAtStartRetriesWithBackoffAndThenKeepsItsInterval()
    {
        _cloudflare.Serve(Api, status: HttpStatusCode.BadGateway);
        _cloudflare.Serve(V4, status: HttpStatusCode.BadGateway);
        _cloudflare.Serve(V6, status: HttpStatusCode.BadGateway);
        using var refresher = Refresher();
        using var cts = new CancellationTokenSource();

        await refresher.StartAsync(cts.Token);
        await WaitUntilAsync(() => _source.ConsecutiveFailures >= 3);
        ServeJson();
        await WaitUntilAsync(() => _source.Current.Origin == EdgeRangeOrigin.Fetched);
        var requests = _cloudflare.Requests.Count;
        await WaitUntilAsync(() => _cloudflare.Requests.Count >= requests + 2);
        await refresher.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, _source.ConsecutiveFailures);
    }

    [Fact]
    public void ThePauseIsTheIntervalAfterAGoodRefreshAndADoublingCappedRetryAfterAFailure()
    {
        using var refresher = Refresher(new CloudflareRangeRefreshOptions
        {
            Interval = TimeSpan.FromDays(1),
            Jitter = TimeSpan.FromHours(1),
            MinRetry = TimeSpan.FromMinutes(5),
            MaxRetry = TimeSpan.FromHours(6),
        });

        for (var i = 0; i < 50; i++)
        {
            Assert.InRange(refresher.Pause(RangeRefreshResult.Replaced, 0), TimeSpan.FromDays(1), TimeSpan.FromDays(1) + TimeSpan.FromHours(1));
            Assert.InRange(refresher.Pause(RangeRefreshResult.Unchanged, 0), TimeSpan.FromDays(1), TimeSpan.FromDays(1) + TimeSpan.FromHours(1));
        }

        foreach (var (failures, baseline) in new[] { (1, 5.0), (2, 10.0), (3, 20.0), (4, 40.0), (7, 320.0), (8, 360.0), (30, 360.0) })
        {
            var pause = refresher.Pause(RangeRefreshResult.Failed, failures);
            var expected = TimeSpan.FromMinutes(baseline);
            Assert.InRange(pause, expected, expected * 1.1);
        }
    }

    [Fact]
    public void TheStoreRoundTripsAndIgnoresADamagedOrDoctoredFile()
    {
        Assert.Null(_file.Load());

        EdgeRangeList.TryCreate([.. ListV4, .. ListV6], Now, out var list, out _);
        _file.Save(list!);
        var loaded = _file.Load();
        Assert.True(loaded!.SameBlocksAs(list!));
        Assert.Equal(Now, loaded.FetchedAt);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));

        // A file with a bad entry, a wrong format, plain garbage, and one that is too large: none is believed.
        foreach (var damaged in new[]
        {
            """{"format":"paduraksa.dev/cloudflare-ranges/v1","fetchedAt":"2026-11-11T00:00:00+00:00","ipv4":["0.0.0.0/0","173.245.48.0/20","103.21.244.0/22","103.22.200.0/22"],"ipv6":["2400:cb00::/32","2606:4700::/32","2803:f800::/32"]}""",
            """{"format":"something-else","fetchedAt":"2026-11-11T00:00:00+00:00","ipv4":[],"ipv6":[]}""",
            """{"format":"paduraksa.dev/cloudflare-ranges/v1"}""",
            "garbage",
            new string(' ', 70_000),
        })
        {
            File.WriteAllText(_file.Path, damaged);
            Assert.Null(_file.Load());
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the condition never held");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Cloudflare as a table of URL to answer; unknown URLs are a 404.</summary>
    private sealed class FakeCloudflare : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<CancellationToken, Task<HttpResponseMessage>>> _answers = [];
        private readonly Lock _lock = new();

        public List<string> Requests { get; } = [];

        public void Serve(string url, string body = "", HttpStatusCode status = HttpStatusCode.OK, TimeSpan? delay = null) =>
            Set(url, async token =>
            {
                if (delay is { } wait)
                {
                    await Task.Delay(wait, token);
                }

                return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) };
            });

        /// <summary>A 200 whose body never ends and carries no Content-Length.</summary>
        public void ServeUnbounded(string url) =>
            Set(url, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndlessStream()) }));

        public void Throw(string url, Exception exception) => Set(url, _ => throw exception);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Func<CancellationToken, Task<HttpResponseMessage>>? answer;
            lock (_lock)
            {
                Requests.Add(request.RequestUri!.ToString());
                _answers.TryGetValue(request.RequestUri.ToString(), out answer);
            }

            return answer is null
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
                : answer(cancellationToken);
        }

        private void Set(string url, Func<CancellationToken, Task<HttpResponseMessage>> answer)
        {
            lock (_lock)
            {
                _answers[url] = answer;
            }
        }
    }

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'x', offset, count);
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
