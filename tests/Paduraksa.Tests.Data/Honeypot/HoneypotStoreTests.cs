using Paduraksa.Application.Honeypot;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Honeypot;
using static Paduraksa.Tests.Data.Honeypot.HoneypotRecordingRig;

namespace Paduraksa.Tests.Data.Honeypot;

/// <summary>The honeypot's tables: what a write stores, how a failed one is kept, and what retention and the caps delete.</summary>
public sealed class HoneypotStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASessionAndItsRequestsAreStoredWithTheClientAddressInFull()
    {
        await using var rig = await HoneypotRecordingRig.CreateAsync();

        await rig.RecordAsync(
            Hit(page: HoneypotPageKind.Index, link: HoneypotLinkKind.Direct, id: 0),
            Hit(link: HoneypotLinkKind.Hidden, id: 5, parent: 0, after: TimeSpan.FromSeconds(2)),
            Hit(page: HoneypotPageKind.NotFound, link: HoneypotLinkKind.Direct, id: 9, status: 404, after: TimeSpan.FromSeconds(3), path: "/archive-k7f3/nothing/", pathTruncated: true));

        var sessions = await rig.Database.QueryAsync(
            "SELECT \"HostKey\", \"ClientIp\", \"UserAgent\", \"Canary\", \"StartedAt\", \"LastSeen\", \"Requests\", \"Pages\", \"GuessedUrls\", \"MaxDepth\", \"LinkHidden\", \"LinkDirect\", \"ReadRobotsFirst\" FROM \"HoneypotSessions\"");
        var session = Assert.Single(sessions);
        Assert.Equal(
            [Host.ToString(), "203.0.113.9", "ExampleBot/1.0", "amber falcon", Start.ToUnixTimeMilliseconds(), Start.AddSeconds(3).ToUnixTimeMilliseconds(), 3L, 2L, 1L, 1L, 1L, 2L, 0L],
            session);
        var requests = await rig.Database.QueryAsync(
            "SELECT \"Status\", \"PageKind\", \"LinkKind\", \"PageKey\", \"ParentKey\", \"Protocol\", \"TlsVersion\", \"Accept\", \"SecFetchUser\", \"Referer\" FROM \"HoneypotRequests\" ORDER BY \"Id\"");
        Assert.Equal(3, requests.Count);
        Assert.Equal([200L, (long)HoneypotPageKind.Index, (long)HoneypotLinkKind.Direct, 0L, 0L, "HTTP/2", (long)System.Security.Authentication.SslProtocols.Tls13, "text/html", "?1", "https://example.test/"], requests[0]);
        Assert.Equal(404L, requests[2][0]);
        var paths = await rig.Database.QueryAsync("SELECT \"Path\", \"PathTruncated\" FROM \"HoneypotRequests\" ORDER BY \"Id\"");
        Assert.Equal([["/archive-k7f3/", 0L], ["/archive-k7f3/", 0L], ["/archive-k7f3/nothing/", 1L]], paths);
    }

    [Fact]
    public async Task ASecondWriteOverwritesTheSessionsTotalsAndOnlyAddsRequests()
    {
        await using var rig = await HoneypotRecordingRig.CreateAsync();
        await rig.RecordAsync(Hit(id: 1));

        await rig.RecordAsync(Hit(id: 2, after: TimeSpan.FromMinutes(1)), Hit(id: 3, after: TimeSpan.FromMinutes(2), link: HoneypotLinkKind.Script));

        Assert.Equal(1L, await rig.Database.ScalarAsync("SELECT count(*) FROM HoneypotSessions"));
        var row = Assert.Single(await rig.Database.QueryAsync("SELECT \"Requests\", \"LinkNormal\", \"LinkScript\", \"JsRuns\", \"LastSeen\" FROM \"HoneypotSessions\""));
        Assert.Equal([3L, 2L, 1L, 1L, Start.AddMinutes(2).ToUnixTimeMilliseconds()], row);
        Assert.Equal(3L, await rig.Database.ScalarAsync("SELECT count(*) FROM HoneypotRequests"));
    }

    [Fact]
    public async Task ABatchBiggerThanOneLookupIsWrittenWhole()
    {
        await using var rig = await HoneypotRecordingRig.CreateAsync();
        var hits = Enumerable.Range(0, 1_300).Select(i => Hit(agent: "agent-" + i, id: (ulong)i)).ToArray();
        await rig.RecordAsync(hits);

        await rig.RecordAsync(Hit(agent: "agent-7", id: 99, after: TimeSpan.FromSeconds(5)));

        Assert.Equal(1_300L, await rig.Database.ScalarAsync("SELECT count(*) FROM HoneypotSessions"));
        Assert.Equal(1_301L, await rig.Database.ScalarAsync("SELECT count(*) FROM HoneypotRequests"));
        Assert.Equal(2L, await rig.Database.ScalarAsync("SELECT \"Requests\" FROM HoneypotSessions WHERE \"UserAgent\" = 'agent-7'"));
    }

    [Fact]
    public async Task AFailedWriteIsKeptAndTheNextPassWritesIt()
    {
        await using var down = HoneypotRecordingRig.Down();
        var hit = Hit();
        down.Recorder.Record(in hit);

        await Assert.ThrowsAnyAsync<Exception>(() => down.Store.WritePendingAsync(Ct));

        Assert.Equal((1, 1), down.Recorder.Waiting);
        await using var up = await HoneypotRecordingRig.CreateAsync();
        // The same batch, now to a database that works.
        var again = down.Recorder.Take();
        await up.Store.WriteAsync(again, Ct);
        Assert.Equal(1L, await up.Database.ScalarAsync("SELECT count(*) FROM HoneypotRequests"));
    }

    [Fact]
    public async Task RowsOlderThanRetentionAreDeletedBySessionsLastRequestAndByRequestTime()
    {
        await using var rig = await HoneypotRecordingRig.CreateAsync();
        await rig.RecordAsync(
            Hit(agent: "old", after: TimeSpan.FromDays(-40)),
            Hit(agent: "long", after: TimeSpan.FromDays(-1), id: 1),
            Hit(agent: "long", after: TimeSpan.FromDays(-1) + TimeSpan.FromMinutes(1), id: 2),
            Hit(agent: "new", after: TimeSpan.FromHours(-1)));
        // A long session whose first request is past retention while its last is not: only a session that ran for 30 days has one.
        await rig.Database.ExecuteAsync($"UPDATE HoneypotRequests SET \"Time\" = {Start.AddDays(-35).ToUnixTimeMilliseconds()} WHERE \"PageKey\" = 1 AND \"SessionId\" IN (SELECT \"Id\" FROM HoneypotSessions WHERE \"UserAgent\" = 'long')");

        var pruned = await rig.Store.PruneAsync(Start, HoneypotRetention.Default, Ct);

        // "old" goes with its request; "long" stays, its latest request being a day old, but loses the request past the age.
        Assert.Equal(new HoneypotPruned(1, 2), pruned);
        var agents = (await rig.Database.QueryAsync("SELECT \"UserAgent\" FROM HoneypotSessions ORDER BY \"UserAgent\"")).Select(static r => (string)r[0]!);
        Assert.Equal(["long", "new"], agents);
        Assert.Equal(2L, await rig.Database.ScalarAsync("SELECT count(*) FROM HoneypotRequests"));
    }

    [Fact]
    public async Task TheSessionCapRemovesTheOldestSessionsWithTheirRequests()
    {
        await using var rig = await HoneypotRecordingRig.CreateAsync();
        for (var i = 0; i < 5; i++)
        {
            await rig.RecordAsync(Hit(agent: "agent-" + i, id: (ulong)i), Hit(agent: "agent-" + i, id: (ulong)(10 + i), after: TimeSpan.FromSeconds(1)));
        }

        var pruned = await rig.Store.PruneAsync(Start, new HoneypotRetention(TimeSpan.FromDays(30), MaxSessions: 3, MaxRequests: 1_000), Ct);

        Assert.Equal(new HoneypotPruned(2, 4), pruned);
        var agents = (await rig.Database.QueryAsync("SELECT \"UserAgent\" FROM HoneypotSessions ORDER BY \"UserAgent\"")).Select(static r => (string)r[0]!);
        Assert.Equal(["agent-2", "agent-3", "agent-4"], agents);
        Assert.Equal(6L, await rig.Database.ScalarAsync("SELECT count(*) FROM HoneypotRequests"));
        Assert.Equal(0L, await rig.Database.ScalarAsync("SELECT count(*) FROM HoneypotRequests WHERE \"SessionId\" NOT IN (SELECT \"Id\" FROM HoneypotSessions)"));
    }

    [Fact]
    public async Task TheRequestCapRemovesTheOldestRequests()
    {
        await using var rig = await HoneypotRecordingRig.CreateAsync();
        await rig.RecordAsync(Enumerable.Range(0, 10).Select(i => Hit(id: (ulong)i, after: TimeSpan.FromSeconds(i))).ToArray());

        var pruned = await rig.Store.PruneAsync(Start, new HoneypotRetention(TimeSpan.FromDays(30), MaxSessions: 100, MaxRequests: 4), Ct);

        Assert.Equal(new HoneypotPruned(0, 6), pruned);
        var pages = (await rig.Database.QueryAsync("SELECT \"PageKey\" FROM HoneypotRequests ORDER BY \"Id\"")).Select(static r => (long)r[0]!);
        Assert.Equal([6L, 7L, 8L, 9L], pages);
        Assert.Equal(1L, await rig.Database.ScalarAsync("SELECT count(*) FROM HoneypotSessions"));
    }

    [Fact]
    public async Task APruneWithinTheCapsDeletesNothing()
    {
        await using var rig = await HoneypotRecordingRig.CreateAsync();
        await rig.RecordAsync(Hit(), Hit(id: 2, after: TimeSpan.FromSeconds(1)));

        Assert.Equal(new HoneypotPruned(0, 0), await rig.Store.PruneAsync(Start, HoneypotRetention.Default, Ct));
    }
}
