using System.Security.Authentication;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Honeypot;
using Paduraksa.ProxyProtocol;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Proxy.Honeypot;

/// <summary>The trap's sink: what it queues, how hits become sessions, and what it keeps bounded.</summary>
public sealed class HoneypotRecorderTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly HostId Host = HostId.New();
    private static readonly IpKey Client = IpKey.Parse("203.0.113.9");
    private const string Agent = "ExampleBot/1.0";

    private readonly ManualTimeProvider _time = new(Start);
    private readonly HoneypotStats _stats = new();

    private HoneypotRecorder Recorder(int capacity = HoneypotRecorder.DefaultQueueCapacity) => new(_stats, _time, capacity);

    private static HoneypotHit Hit(
        HoneypotPageKind page = HoneypotPageKind.Post,
        HoneypotLinkKind link = HoneypotLinkKind.Normal,
        TimeSpan? after = null,
        ulong id = 1,
        ulong parent = 0,
        HoneypotPowOutcome pow = HoneypotPowOutcome.None,
        string agent = Agent,
        IpKey? client = null,
        HostId? host = null,
        string canary = "amber falcon",
        int status = 200,
        string path = "/archive-k7f3/",
        bool pathTruncated = false) =>
        new(
            Start + (after ?? TimeSpan.Zero), host ?? Host, client ?? Client, agent, canary, HoneypotEntry.Robots, page, link, id, parent, status, pow, 0,
            "HTTP/1.1", SslProtocols.Tls13, "text/html", "en", "none", "navigate", "document", "?1", "https://example.test/", path, pathTruncated);

    private static HoneypotSessionRecord Only(HoneypotBatch batch) => Assert.Single(batch.Sessions);

    [Fact]
    public void AFullQueueDropsTheHitAndCountsItWithoutWaiting()
    {
        var recorder = Recorder(capacity: 2);

        for (var i = 0; i < 5; i++)
        {
            recorder.Record(Hit(id: (ulong)i));
        }

        Assert.Equal(3, _stats.Dropped);
        recorder.Drain();
        Assert.Equal(2, recorder.Take().Requests.Count);
        // Room again once drained.
        recorder.Record(Hit());
        Assert.Equal(3, _stats.Dropped);
    }

    [Fact]
    public void ASessionEndsAfterThirtyIdleMinutesAndTheNextRequestStartsAnother()
    {
        var recorder = Recorder();
        recorder.Record(Hit());
        recorder.Record(Hit(after: TimeSpan.FromMinutes(29) + TimeSpan.FromSeconds(59)));
        recorder.Record(Hit(after: TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(58)));
        recorder.Record(Hit(after: TimeSpan.FromMinutes(89) + TimeSpan.FromSeconds(58)));
        recorder.Drain();

        var batch = recorder.Take();

        Assert.Equal(2, batch.Sessions.Count);
        var first = batch.Sessions.OrderBy(static s => s.StartedAt).First();
        var second = batch.Sessions.OrderBy(static s => s.StartedAt).Last();
        Assert.Equal(3, first.Requests);
        Assert.Equal(1, second.Requests);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(Start, first.StartedAt);
        Assert.Equal(Start + TimeSpan.FromMinutes(89) + TimeSpan.FromSeconds(58), second.LastSeen);
        Assert.Equal([first.Id, first.Id, first.Id, second.Id], batch.Requests.Select(static r => r.SessionId));
    }

    [Fact]
    public void ARequestExactlyThirtyMinutesAfterTheLastIsANewSession()
    {
        var recorder = Recorder();
        recorder.Record(Hit());
        recorder.Record(Hit(after: TimeSpan.FromMinutes(30)));
        recorder.Drain();

        Assert.Equal(2, recorder.Take().Sessions.Count);
    }

    [Fact]
    public void AnotherUserAgentAddressOrHostIsAnotherSession()
    {
        var recorder = Recorder();
        recorder.Record(Hit());
        recorder.Record(Hit(agent: "OtherBot/2"));
        recorder.Record(Hit(client: IpKey.Parse("203.0.113.10")));
        recorder.Record(Hit(client: IpKey.Parse("2001:db8::1")));
        recorder.Record(Hit(host: HostId.New()));
        recorder.Record(Hit());
        recorder.Drain();

        var batch = recorder.Take();

        Assert.Equal(5, batch.Sessions.Count);
        Assert.Equal(2, batch.Sessions.Single(static s => s.UserAgent == Agent && s.ClientIp == Client && s.Host == Host).Requests);
    }

    [Fact]
    public void CountersFollowTheKindOfEachRequest()
    {
        var recorder = Recorder();
        recorder.Record(Hit(HoneypotPageKind.Index, HoneypotLinkKind.Direct, id: 0));
        recorder.Record(Hit(HoneypotPageKind.Post, HoneypotLinkKind.Hidden, id: 5));
        recorder.Record(Hit(HoneypotPageKind.Post, HoneypotLinkKind.Hidden, id: 6));
        recorder.Record(Hit(HoneypotPageKind.Category, HoneypotLinkKind.NoIndexPage, id: 7));
        recorder.Record(Hit(HoneypotPageKind.Sitemap, HoneypotLinkKind.Direct, id: 8));
        recorder.Record(Hit(HoneypotPageKind.Feed, HoneypotLinkKind.Feed, id: 9));
        recorder.Record(Hit(HoneypotPageKind.Image, HoneypotLinkKind.Image, id: 10));
        recorder.Record(Hit(HoneypotPageKind.Beacon, HoneypotLinkKind.Beacon, id: 11));
        recorder.Record(Hit(HoneypotPageKind.Post, HoneypotLinkKind.Script, id: 12));
        recorder.Record(Hit(HoneypotPageKind.NotFound, HoneypotLinkKind.Direct, id: 13, status: 404));
        recorder.Drain();

        var session = Only(recorder.Take());

        Assert.Equal(10, session.Requests);
        // Index, two hidden posts, a category and the script-linked post.
        Assert.Equal(5, session.Pages);
        Assert.Equal(1, session.GuessedUrls);
        Assert.Equal(1, session.Images);
        // The beacon and the script link.
        Assert.Equal(2, session.JsRuns);
        Assert.Equal(1, session.LinkCounts[(int)HoneypotLinkKind.Feed]);
        Assert.Equal(2, session.LinkCounts[(int)HoneypotLinkKind.Hidden]);
        Assert.Equal(1, session.LinkCounts[(int)HoneypotLinkKind.NoIndexPage]);
        Assert.Equal(3, session.LinkCounts[(int)HoneypotLinkKind.Direct]);
        Assert.Equal(1, session.LinkCounts[(int)HoneypotLinkKind.Image]);
        Assert.Equal(1, session.LinkCounts[(int)HoneypotLinkKind.Script]);
        Assert.Equal(0, session.LinkCounts[(int)HoneypotLinkKind.Comment]);
        Assert.Equal(session.Requests, session.LinkCounts.Sum());
    }

    [Fact]
    public void TheChallengeIsCountedShownSolvedAndPassed()
    {
        var recorder = Recorder();
        recorder.Record(Hit(id: 20, pow: HoneypotPowOutcome.Shown, status: 200));
        recorder.Record(Hit(id: 20, pow: HoneypotPowOutcome.Solved, status: 303));
        recorder.Record(Hit(id: 20, pow: HoneypotPowOutcome.Passed));
        recorder.Drain();

        var session = Only(recorder.Take());

        Assert.Equal(3, session.Requests);
        Assert.Equal(1, session.ChallengesShown);
        Assert.Equal(1, session.ChallengesSolved);
        Assert.Equal(1, session.ChallengesPassed);
        // The shown challenge and the served page are two requests for the page; the verify request is neither a page nor a link followed.
        Assert.Equal(2, session.Pages);
        Assert.Equal(2, session.LinkCounts[(int)HoneypotLinkKind.Normal]);
    }

    [Fact]
    public void DepthIsTheLinksFollowedFromTheIndexAndTheStoredValueIsTheDeepest()
    {
        var recorder = Recorder();
        recorder.Record(Hit(HoneypotPageKind.Index, HoneypotLinkKind.Direct, id: 0));
        recorder.Record(Hit(HoneypotPageKind.Post, id: 100, parent: 0));
        recorder.Record(Hit(HoneypotPageKind.Category, id: 101, parent: 100));
        recorder.Record(Hit(HoneypotPageKind.Post, id: 102, parent: 101));
        // Back to a shallow page: the maximum stays.
        recorder.Record(Hit(HoneypotPageKind.Post, id: 103, parent: 0));
        // The parent was never seen by this session: one link from something unseen.
        recorder.Record(Hit(HoneypotPageKind.Post, id: 104, parent: 999));
        recorder.Drain();

        Assert.Equal(3, Only(recorder.Take()).MaxDepth);
    }

    [Fact]
    public void AChallengeThatWasNeverPassedLeadsNowhereAndAGuessedUrlIsNoDepth()
    {
        var recorder = Recorder();
        recorder.Record(Hit(HoneypotPageKind.Index, HoneypotLinkKind.Direct, id: 0));
        recorder.Record(Hit(HoneypotPageKind.Post, id: 100, parent: 0, pow: HoneypotPowOutcome.Shown));
        recorder.Record(Hit(HoneypotPageKind.NotFound, HoneypotLinkKind.Direct, id: 7, parent: 100, status: 404));
        recorder.Record(Hit(HoneypotPageKind.Post, id: 105, parent: 100));
        recorder.Drain();

        // The post behind the challenge was not served, so the post linked from it is one link from something unseen.
        Assert.Equal(1, Only(recorder.Take()).MaxDepth);
    }

    [Fact]
    public void ASessionRemembersAtMostAFixedNumberOfPagesForItsDepth()
    {
        var recorder = Recorder();
        // A chain deeper than the memory: pages past it are forgotten, and what hangs off them counts from one.
        for (var i = 0; i < HoneypotRecorder.MaxPagesPerSession + 10; i++)
        {
            recorder.Record(Hit(id: (ulong)(1_000 + i), parent: i == 0 ? 0 : (ulong)(1_000 + i - 1)));
        }

        recorder.Drain();

        // The last remembered page is at depth 256, and the first page past the memory hangs off it; the ones after it hang off a page nobody remembers.
        Assert.Equal(HoneypotRecorder.MaxPagesPerSession + 1, Only(recorder.Take()).MaxDepth);
    }

    [Fact]
    public void ASessionRecordsWhetherTheVisitorReadRobotsFirst()
    {
        var recorder = Recorder();
        recorder.RobotsFetched(Host, Client, Agent);
        _time.Now = Start.AddSeconds(1);
        recorder.Record(Hit(after: TimeSpan.FromSeconds(2)));
        // Same address, another agent: it never fetched the file.
        recorder.Record(Hit(agent: "OtherBot/2", after: TimeSpan.FromSeconds(2)));
        // Fetched, but after the session began.
        recorder.Record(Hit(client: IpKey.Parse("198.51.100.1"), after: TimeSpan.FromSeconds(2)));
        recorder.RobotsFetched(Host, IpKey.Parse("198.51.100.1"), Agent);
        recorder.Drain();

        var sessions = recorder.Take().Sessions;

        Assert.True(sessions.Single(static s => s.UserAgent == Agent && s.ClientIp == Client).ReadRobotsFirst);
        Assert.False(sessions.Single(static s => s.UserAgent == "OtherBot/2").ReadRobotsFirst);
        Assert.False(sessions.Single(static s => s.ClientIp == IpKey.Parse("198.51.100.1")).ReadRobotsFirst);
    }

    [Fact]
    public void ARobotsFetchIsRememberedForADayAndNoLonger()
    {
        var recorder = Recorder();
        recorder.RobotsFetched(Host, Client, Agent);
        recorder.Record(Hit(after: TimeSpan.FromHours(23)));
        recorder.Record(Hit(agent: "Late/1", after: TimeSpan.FromHours(25)));
        recorder.RobotsFetched(Host, Client, "Late/1");
        recorder.Drain();

        var sessions = recorder.Take().Sessions;

        Assert.True(sessions.Single(static s => s.UserAgent == Agent).ReadRobotsFirst);
        Assert.False(sessions.Single(static s => s.UserAgent == "Late/1").ReadRobotsFirst);
    }

    [Fact]
    public void TheRobotsMemoryIsBounded()
    {
        var recorder = Recorder(capacity: HoneypotRecorder.MaxRememberedRobotsFetches + 10);
        for (var i = 0; i < HoneypotRecorder.MaxRememberedRobotsFetches + 5; i++)
        {
            recorder.RobotsFetched(Host, Client, "agent-" + i);
        }

        recorder.Record(Hit(agent: "agent-0"));
        recorder.Record(Hit(agent: "agent-" + (HoneypotRecorder.MaxRememberedRobotsFetches + 4)));
        recorder.Drain();

        var sessions = recorder.Take().Sessions;
        // The oldest was forgotten, the newest is there.
        Assert.False(sessions.Single(static s => s.UserAgent == "agent-0").ReadRobotsFirst);
        Assert.True(sessions.Single(static s => s.UserAgent == "agent-" + (HoneypotRecorder.MaxRememberedRobotsFetches + 4)).ReadRobotsFirst);
    }

    [Fact]
    public void TheCanaryPhraseIsStoredWithTheSessionOnceAHitCarriesIt()
    {
        var recorder = Recorder();
        recorder.Record(Hit(canary: string.Empty));
        recorder.Record(Hit(canary: "amber falcon"));
        recorder.Record(Hit(canary: "amber falcon"));
        recorder.Drain();

        Assert.Equal("amber falcon", Only(recorder.Take()).Canary);
    }

    [Fact]
    public void WhatAClientSendsIsCutToTheLengthsTheTablesKeep()
    {
        var recorder = Recorder();
        var hit = Hit() with { UserAgent = new string('u', 2_000), Referer = new string('r', 2_000), Accept = new string('a', 2_000), AcceptLanguage = new string('l', 2_000), SecFetchMode = new string('m', 500) };
        recorder.Record(hit);
        recorder.Drain();

        var batch = recorder.Take();

        Assert.Equal(HoneypotLimits.RecordedUserAgentLength, Only(batch).UserAgent.Length);
        var request = Assert.Single(batch.Requests).Hit;
        Assert.Equal(HoneypotLimits.RecordedUserAgentLength, request.UserAgent.Length);
        Assert.Equal(HoneypotLimits.RecordedRefererLength, request.Referer!.Length);
        Assert.Equal(HoneypotLimits.RecordedAcceptLength, request.Accept!.Length);
        Assert.Equal(HoneypotLimits.RecordedAcceptLanguageLength, request.AcceptLanguage!.Length);
        Assert.Equal(HoneypotLimits.RecordedSecFetchLength, request.SecFetchMode!.Length);
    }

    [Fact]
    public void ARequestKeepsItsPathAndAnOverlongOneIsCutAndMarked()
    {
        var recorder = Recorder();
        recorder.Record(Hit(path: "/wp-login.php"));
        recorder.Record(Hit(path: "/archive-k7f3/x", pathTruncated: true));
        recorder.Record(Hit(path: new string('p', 900)));
        recorder.Drain();

        var requests = recorder.Take().Requests.Select(static r => r.Hit).ToArray();

        Assert.Equal(("/wp-login.php", false), (requests[0].Path, requests[0].PathTruncated));
        Assert.True(requests[1].PathTruncated);
        Assert.Equal((HoneypotLimits.RecordedPathLength, true), (requests[2].Path.Length, requests[2].PathTruncated));
    }

    [Fact]
    public void ASessionIsRecordedOnceADrainWithItsLatestTotals()
    {
        var recorder = Recorder();
        recorder.Record(Hit(id: 1));
        recorder.Drain();
        recorder.Record(Hit(id: 2));
        recorder.Record(Hit(id: 3));
        recorder.Drain();

        var batch = recorder.Take();

        Assert.Equal(3, Only(batch).Requests);
        Assert.Equal(3, batch.Requests.Count);
        Assert.True(recorder.Take().IsEmpty);
    }

    [Fact]
    public void AFailedWriteIsPutBackAheadOfWhatCameInMeanwhile()
    {
        var recorder = Recorder();
        recorder.Record(Hit(id: 1));
        recorder.Drain();
        var failed = recorder.Take();
        recorder.Record(Hit(id: 2));
        recorder.Drain();

        recorder.Requeue(failed);
        var batch = recorder.Take();

        // The newer record of the session wins, and the requests keep their order.
        Assert.Equal(2, Only(batch).Requests);
        Assert.Equal([1UL, 2UL], batch.Requests.Select(static r => r.Hit.PageId));
    }

    [Fact]
    public void AnEndedSessionNobodyWroteYetIsStillWaitingAfterAFailedWrite()
    {
        var recorder = Recorder();
        recorder.Record(Hit());
        recorder.Drain();
        var failed = recorder.Take();
        _time.Now = Start.AddHours(2);
        recorder.Drain();
        Assert.Equal(0, recorder.ActiveSessions);

        recorder.Requeue(failed);

        Assert.Single(recorder.Take().Sessions);
    }

    [Fact]
    public void IdleSessionsLeaveMemoryAtTheNextDrain()
    {
        var recorder = Recorder();
        recorder.Record(Hit());
        recorder.Drain();
        Assert.Equal(1, recorder.ActiveSessions);

        _time.Now = Start.AddMinutes(30);
        recorder.Drain();

        Assert.Equal(0, recorder.ActiveSessions);
    }

    [Fact]
    public void TooManyActiveSessionsEndTheLeastRecentlySeen()
    {
        var recorder = Recorder(capacity: HoneypotRecorder.MaxActiveSessions + 10);
        for (var i = 0; i < HoneypotRecorder.MaxActiveSessions + 1; i++)
        {
            recorder.Record(Hit(agent: "agent-" + i, after: TimeSpan.FromMilliseconds(i)));
        }

        recorder.Drain();

        Assert.True(recorder.ActiveSessions <= HoneypotRecorder.MaxActiveSessions);
        // An ended session's next request begins a new one, which is the cost of the cap.
        recorder.Record(Hit(agent: "agent-0", after: TimeSpan.FromSeconds(30)));
        recorder.Drain();
        Assert.Equal(HoneypotRecorder.MaxActiveSessions + 2, recorder.Take().Sessions.Count);
    }

    [Fact]
    public void RowsWaitingForADatabaseThatIsDownAreBoundedAndTheOldestGoFirst()
    {
        var recorder = Recorder(capacity: HoneypotRecorder.MaxWaitingRequests + 100);
        for (var i = 0; i < HoneypotRecorder.MaxWaitingRequests + 100; i++)
        {
            recorder.Record(Hit(id: (ulong)i));
        }

        recorder.Drain();

        var (_, requests) = recorder.Waiting;
        Assert.True(requests <= HoneypotRecorder.MaxWaitingRequests);
        Assert.Equal(HoneypotRecorder.MaxWaitingRequests + 100 - requests, _stats.Dropped);
        var batch = recorder.Take();
        Assert.Equal((ulong)(HoneypotRecorder.MaxWaitingRequests + 99), batch.Requests[^1].Hit.PageId);
        Assert.True(batch.Requests[0].Hit.PageId > 0);
    }

    [Fact]
    public void QueueingAHitOrARobotsFetchAllocatesNothing()
    {
        // The robots.txt answer is a 0 B path, and it tells the sink on a host with a trap: the queue must cost the request nothing.
        var recorder = Recorder(capacity: 100_000);
        var hit = Hit();

        AllocAssert.Zero((recorder, hit), static s => s.recorder.Record(in s.hit), "Record");
        AllocAssert.Zero(recorder, static r => r.RobotsFetched(Host, Client, Agent), "RobotsFetched");
    }

    [Fact]
    public async Task RecordingNeverBlocksWhileTheReaderIsBusy()
    {
        var recorder = Recorder();
        var drain = Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                recorder.Drain();
            }
        }, TestContext.Current.CancellationToken);

        for (var i = 0; i < 20_000; i++)
        {
            recorder.Record(Hit(id: (ulong)i));
        }

        await drain;
        recorder.Drain();

        var batch = recorder.Take();
        Assert.Equal(20_000 - _stats.Dropped, batch.Requests.Count);
    }
}
