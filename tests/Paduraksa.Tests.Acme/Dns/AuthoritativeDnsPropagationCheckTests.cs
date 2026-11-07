using System.Net;
using Paduraksa.Acme.Dns;
using Paduraksa.Tests.Acme.Fakes;
using static Paduraksa.Tests.Acme.Fakes.AcmeHarness;

namespace Paduraksa.Tests.Acme.Dns;

/// <summary>
/// The propagation check against a fake resolver and two fake authoritative servers (127.0.0.2 and 127.0.0.3, one port), over
/// real UDP and TCP. The pauses between rounds run on a virtual clock where a test is about timing, and in a few milliseconds of real
/// time where it is about a server catching up.
/// </summary>
public sealed class AuthoritativeDnsPropagationCheckTests : IDisposable
{
    private const string Challenge = "_acme-challenge.example.com";

    private readonly FakeDnsServer _resolver = new();
    private readonly List<FakeDnsServer> _servers;
    private readonly VirtualTimeProvider _virtualTime = new();

    public AuthoritativeDnsPropagationCheckTests()
    {
        _servers = FakeDnsServer.TryCreateGroup(2) ?? [];
        if (_servers.Count == 0)
        {
            Assert.Skip("This machine does not listen on 127.0.0.2 and 127.0.0.3.");
        }

        _resolver.AddNs("example.com", "ns1.example.com");
        _resolver.AddNs("example.com", "ns2.example.com");
        _resolver.AddA("ns1.example.com", _servers[0].EndPoint.Address);
        _resolver.AddA("ns2.example.com", _servers[1].EndPoint.Address);
    }

    public void Dispose()
    {
        _resolver.Dispose();
        _servers.ForEach(s => s.Dispose());
    }

    private DnsPropagationOptions Options(DnsPropagationOptions? from = null) =>
        (from ?? new DnsPropagationOptions()) with
        {
            Resolvers = [_resolver.EndPoint],
            AuthoritativePort = _servers[0].EndPoint.Port,
            QueryTimeout = TimeSpan.FromMilliseconds(300),
        };

    private DnsPropagationOptions Fast() =>
        Options(new DnsPropagationOptions { InitialInterval = TimeSpan.FromMilliseconds(20), MaxInterval = TimeSpan.FromMilliseconds(40), Timeout = TimeSpan.FromSeconds(10) });

    private static IReadOnlyList<DnsChallengeRecord> Records(params string[] values) => [.. values.Select(v => new DnsChallengeRecord(Challenge, v))];

    [Fact]
    public async Task PropagatedWhenEveryAuthoritativeServerReturnsTheValue()
    {
        _servers.ForEach(s => s.AddTxt(Challenge, "v1"));

        DnsPropagationResult result = await new AuthoritativeDnsPropagationCheck(Fast()).WaitAsync(Records("v1"), Token);

        Assert.Equal(DnsPropagationOutcome.Propagated, result.Outcome);
        Assert.Contains("2 name server(s)", result.Detail, StringComparison.Ordinal);
        // Each authoritative server was asked once, directly and without recursion; the resolver was asked only for NS and addresses.
        Assert.All(_servers, s => Assert.Equal(new FakeQuery(s.Queries[0].Id, Challenge, 16, false, false), s.Queries[0]));
        Assert.All(_servers, s => Assert.Single(s.Queries));
        Assert.DoesNotContain(_resolver.Queries, q => q.Type == 16);
        Assert.All(_resolver.Queries, q => Assert.True(q.RecursionDesired));
    }

    [Fact]
    public async Task ALaggingServerIsWaitedForUntilItHasTheValue()
    {
        _servers[0].AddTxt(Challenge, "v1");
        Task late = Task.Run(async () =>
        {
            await Task.Delay(250, Token);
            _servers[1].AddTxt(Challenge, "v1");
        }, Token);

        DnsPropagationResult result = await new AuthoritativeDnsPropagationCheck(Fast()).WaitAsync(Records("v1"), Token);
        await late;

        Assert.Equal(DnsPropagationOutcome.Propagated, result.Outcome);
        Assert.True(_servers[1].Queries.Count > 1, "the lagging server should have been asked again");
        // The server that already had it is not asked again.
        Assert.Single(_servers[0].Queries);
    }

    [Fact]
    public async Task EveryValueAtASharedNameMustBeThere()
    {
        _servers.ForEach(s => s.AddTxt(Challenge, "wildcard-value"));
        _servers[1].AddTxt(Challenge, "apex-value");

        DnsPropagationResult result = await new AuthoritativeDnsPropagationCheck(Options(new DnsPropagationOptions { Timeout = TimeSpan.FromSeconds(30) }), _virtualTime)
            .WaitAsync(Records("wildcard-value", "apex-value"), Token);

        Assert.Equal(DnsPropagationOutcome.TimedOut, result.Outcome);
        Assert.Contains("ns1.example.com", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("ns2.example.com", result.Detail, StringComparison.Ordinal);

        _servers[0].AddTxt(Challenge, "apex-value");
        Assert.Equal(DnsPropagationOutcome.Propagated, (await new AuthoritativeDnsPropagationCheck(Fast()).WaitAsync(Records("wildcard-value", "apex-value"), Token)).Outcome);
    }

    [Fact]
    public async Task ATextSplitIntoStringsIsMatchedAsOneValue()
    {
        _servers.ForEach(s => s.Add(Challenge, 16, DnsMessages.Txt("v", "1")));

        DnsPropagationResult result = await new AuthoritativeDnsPropagationCheck(Fast()).WaitAsync(Records("v1"), Token);

        Assert.Equal(DnsPropagationOutcome.Propagated, result.Outcome);
    }

    [Fact]
    public async Task TheWaitBacksOffAndEndsAtTheTimeout()
    {
        DnsPropagationResult result = await new AuthoritativeDnsPropagationCheck(
            Options(new DnsPropagationOptions { Timeout = TimeSpan.FromSeconds(60), InitialInterval = TimeSpan.FromSeconds(2), MaxInterval = TimeSpan.FromSeconds(15), BackoffFactor = 2 }),
            _virtualTime).WaitAsync(Records("v1"), Token);

        Assert.Equal(DnsPropagationOutcome.TimedOut, result.Outcome);
        Assert.Equal(
            [2, 4, 8, 15, 15, 15],
            _virtualTime.Delays.Take(6).Select(d => (int)d.TotalSeconds));
        Assert.True(_virtualTime.Delays.Sum(d => d.TotalSeconds) <= 60);
        Assert.Equal(60, _virtualTime.Delays.Sum(d => d.TotalSeconds), 0.5);
    }

    [Fact]
    public async Task ServersThatAnswerWithoutAuthorityAreLeftOutWhenOthersAreDone()
    {
        _servers[0].AddTxt(Challenge, "v1");
        _servers[1].Authoritative = false;
        _servers[1].AddTxt(Challenge, "v1");

        DnsPropagationResult result = await new AuthoritativeDnsPropagationCheck(Fast()).WaitAsync(Records("v1"), Token);

        Assert.Equal(DnsPropagationOutcome.Propagated, result.Outcome);
        Assert.Contains("1 name server(s)", result.Detail, StringComparison.Ordinal);
        Assert.Contains("skipped", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IfNoServerIsAuthoritativeTheCheckCannotBeMade()
    {
        _servers.ForEach(s =>
        {
            s.Authoritative = false;
            s.AddTxt(Challenge, "v1");
        });

        DnsPropagationResult result = await new AuthoritativeDnsPropagationCheck(Fast()).WaitAsync(Records("v1"), Token);

        Assert.Equal(DnsPropagationOutcome.Unverifiable, result.Outcome);
    }

    [Fact]
    public async Task ASilentServerIsLeftOutAfterThreeRoundsAndRefusalsAreNotAuthority()
    {
        _servers[0].AddTxt(Challenge, "v1");
        _servers[1].Override = _ => [];
        DnsPropagationOptions options = Fast() with { QueryTimeout = TimeSpan.FromMilliseconds(50) };

        DnsPropagationResult result = await new AuthoritativeDnsPropagationCheck(options).WaitAsync(Records("v1"), Token);

        Assert.Equal(DnsPropagationOutcome.Propagated, result.Outcome);
        Assert.Equal(3, _servers[1].Queries.Count);

        _servers[1].Override = null;
        _servers[1].ResponseCode = 5;
        Assert.Equal(DnsPropagationOutcome.Propagated, (await new AuthoritativeDnsPropagationCheck(options).WaitAsync(Records("v1"), Token)).Outcome);
    }

    [Fact]
    public async Task AZoneNobodyHasNameServersForCannotBeChecked()
    {
        _resolver.RemoveAll("example.com", 2);

        DnsPropagationResult result = await new AuthoritativeDnsPropagationCheck(Fast()).WaitAsync(Records("v1"), Token);

        Assert.Equal(DnsPropagationOutcome.Unverifiable, result.Outcome);
        Assert.Contains("could not be found", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AResolverThatIsDownCannotBeChecked()
    {
        IPEndPoint closed;
        using (FakeDnsServer gone = new())
        {
            closed = gone.EndPoint;
        }

        DnsPropagationResult result = await new AuthoritativeDnsPropagationCheck(Fast() with { Resolvers = [closed] }).WaitAsync(Records("v1"), Token);

        Assert.Equal(DnsPropagationOutcome.Unverifiable, result.Outcome);
    }

    [Fact]
    public async Task AnotherResolverIsTriedWhenTheFirstIsDown()
    {
        IPEndPoint closed;
        using (FakeDnsServer gone = new())
        {
            closed = gone.EndPoint;
        }

        _servers.ForEach(s => s.AddTxt(Challenge, "v1"));

        DnsPropagationResult result = await new AuthoritativeDnsPropagationCheck(Fast() with { Resolvers = [closed, _resolver.EndPoint] }).WaitAsync(Records("v1"), Token);

        Assert.Equal(DnsPropagationOutcome.Propagated, result.Outcome);
    }

    [Fact]
    public async Task TheZoneIsFoundByWalkingUpFromTheRecordName()
    {
        _servers.ForEach(s => s.AddTxt("_acme-challenge.a.b.example.com", "v1"));

        DnsPropagationResult result = await new AuthoritativeDnsPropagationCheck(Fast())
            .WaitAsync([new DnsChallengeRecord("_acme-challenge.a.b.example.com.", "v1")], Token);

        Assert.Equal(DnsPropagationOutcome.Propagated, result.Outcome);
        Assert.Equal(
            ["_acme-challenge.a.b.example.com", "a.b.example.com", "b.example.com", "example.com"],
            _resolver.Queries.Where(q => q.Type == 2).Select(q => q.Name));
    }

    [Fact]
    public async Task ATruncatedUdpAnswerIsReadOverTcp()
    {
        _servers.ForEach(s =>
        {
            s.TruncateUdp = true;
            s.AddTxt(Challenge, "v1");
        });

        DnsPropagationResult result = await new AuthoritativeDnsPropagationCheck(Fast()).WaitAsync(Records("v1"), Token);

        Assert.Equal(DnsPropagationOutcome.Propagated, result.Outcome);
        Assert.All(_servers, s => Assert.Equal([false, true], s.Queries.Select(q => q.Tcp)));
    }

    [Fact]
    public async Task ARecordTheCheckCannotSeeIsNeverReportedFromAForgedOrMismatchedAnswer()
    {
        // A server that answers with the right text but for another name, or with a wrong id, must not count.
        _servers.ForEach(s => s.Override = q =>
        [
            DnsMessages.Response((ushort)(q.Id ^ 1), q.Name, q.Type, [DnsMessages.Record(DnsMessages.QuestionPointer, 16, DnsMessages.Txt("v1"))]),
            DnsMessages.Response(q.Id, "other.example.com", q.Type, [DnsMessages.Record(DnsMessages.QuestionPointer, 16, DnsMessages.Txt("v1"))]),
        ]);

        DnsPropagationResult result = await new AuthoritativeDnsPropagationCheck(Fast() with { QueryTimeout = TimeSpan.FromMilliseconds(50) }).WaitAsync(Records("v1"), Token);

        Assert.Equal(DnsPropagationOutcome.Unverifiable, result.Outcome);
    }

    [Fact]
    public async Task AnAnswerForTheWrongOwnerNameDoesNotCount()
    {
        _servers.ForEach(s => s.Override = q =>
        [
            DnsMessages.Response(q.Id, q.Name, q.Type, [DnsMessages.Record(DnsMessages.Name("elsewhere.example.com"), 16, DnsMessages.Txt("v1"))]),
        ]);

        DnsPropagationResult result = await new AuthoritativeDnsPropagationCheck(Options(new DnsPropagationOptions { Timeout = TimeSpan.FromSeconds(10) }), _virtualTime)
            .WaitAsync(Records("v1"), Token);

        Assert.Equal(DnsPropagationOutcome.TimedOut, result.Outcome);
    }

    [Fact]
    public async Task CancellationStopsTheWait()
    {
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new AuthoritativeDnsPropagationCheck(Fast()).WaitAsync(Records("v1"), cancel.Token));
    }

    [Fact]
    public async Task NothingToLookForIsPropagatedAndABadRecordIsRefused()
    {
        AuthoritativeDnsPropagationCheck check = new(Fast());

        Assert.Equal(DnsPropagationOutcome.Propagated, (await check.WaitAsync([], Token)).Outcome);
        await Assert.ThrowsAsync<ArgumentException>(async () => await check.WaitAsync([new DnsChallengeRecord("bad name", "v")], Token));
    }

    [Fact]
    public async Task RecordsInTwoZonesAreEachCheckedAtTheirOwnServers()
    {
        _resolver.AddNs("example.org", "ns1.example.com");
        _servers.ForEach(s => s.AddTxt(Challenge, "v1"));
        _servers[0].AddTxt("_acme-challenge.example.org", "v2");

        DnsPropagationResult result = await new AuthoritativeDnsPropagationCheck(Fast() with { Timeout = TimeSpan.FromSeconds(10) })
            .WaitAsync([new DnsChallengeRecord(Challenge, "v1"), new DnsChallengeRecord("_acme-challenge.example.org", "v2")], Token);

        Assert.Equal(DnsPropagationOutcome.Propagated, result.Outcome);
    }

    [Fact]
    public async Task RecordsAddedToAZoneAfterItsServersWereSatisfiedAreStillChecked()
    {
        // Two names in one zone. The second name's zone lookup fails in round one (its first NS query is lost), so the first name's
        // record is already satisfied everywhere when the second joins the zone; it must still be asked for.
        const string Second = "_acme-challenge.a.example.com";
        _servers.ForEach(s => s.AddTxt(Challenge, "v1"));
        bool dropped = false;
        _resolver.Override = q =>
        {
            if (!dropped && q.Type == 2 && q.Name == Second)
            {
                dropped = true;
                return [];
            }

            return null;
        };

        DnsPropagationResult result = await new AuthoritativeDnsPropagationCheck(Fast() with { Timeout = TimeSpan.FromSeconds(1) })
            .WaitAsync([new DnsChallengeRecord(Challenge, "v1"), new DnsChallengeRecord(Second, "v2")], Token);

        // No server has the second record, so this must not be reported as propagated, and it must have been asked about.
        Assert.Equal(DnsPropagationOutcome.TimedOut, result.Outcome);
        Assert.True(result.AnyServerHadRecords);
        Assert.All(_servers, s => Assert.Contains(s.Queries, q => q.Name == Second));
    }

    [Fact]
    public async Task ExcludedServersAreNotAskedAgain()
    {
        // One server never answers; the other never has the record, so the wait runs to its timeout on the virtual clock.
        _servers[1].Override = _ => [];

        DnsPropagationResult result = await new AuthoritativeDnsPropagationCheck(
            Options(new DnsPropagationOptions { Timeout = TimeSpan.FromSeconds(60) }) with { QueryTimeout = TimeSpan.FromMilliseconds(50) },
            _virtualTime).WaitAsync(Records("v1"), Token);

        Assert.Equal(DnsPropagationOutcome.TimedOut, result.Outcome);
        Assert.True(_servers[0].Queries.Count > 5);
        Assert.Equal(3, _servers[1].Queries.Count);
    }

    [Fact]
    public async Task ATimeoutWithNoServerHoldingAnyRecordSaysSo()
    {
        DnsPropagationResult result = await new AuthoritativeDnsPropagationCheck(Options(new DnsPropagationOptions { Timeout = TimeSpan.FromSeconds(20) }), _virtualTime)
            .WaitAsync(Records("v1"), Token);

        Assert.Equal(DnsPropagationOutcome.TimedOut, result.Outcome);
        Assert.False(result.AnyServerHadRecords);
    }
}
