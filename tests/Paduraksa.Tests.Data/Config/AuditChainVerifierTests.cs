using Paduraksa.Application.Audit;
using Paduraksa.Application.Revisions;
using Paduraksa.Core.Model;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Config;

/// <summary>Tampering with the audit table the way someone with SQL access would, and the verifier catching it.</summary>
public sealed class AuditChainVerifierTests : IAsyncLifetime
{
    private const int Events = 6;

    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private TestConfigDatabase _db = null!;
    private List<AuditRecord> _events = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await CreateAsync();
        var clock = new ManualTimeProvider(Now);
        var log = new AuditLog(clock);
        ConfigRevisionId revision;
        await using (var context = _db.NewContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            revision = (await new ConfigRevisionStore(clock).AppendAsync(context, new NewRevision("{}"u8.ToArray(), "[]", Actor.Cli, Channel.Cli), Ct)).Id;
            await transaction.CommitAsync(Ct);
        }

        _events = [];
        for (var i = 0; i < Events; i++)
        {
            await using var context = _db.NewContext();
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            // Event 2, the one the field edits target, is a token actor's and committed a revision.
            var actor = i == 2 ? Actor.ForToken(ApiTokenId.New()) : Actor.ForUser(UserId.New());
            var entry = new AuditEntry(actor, Channel.Api, "config.host.update", AuditOutcome.Success)
            {
                Details = $$"""{"n":{{i}}}""",
                RevisionId = i == 2 ? revision : null,
            }.WithTarget(HostId.New());
            _events.Add(await log.AppendAsync(context, entry, Ct));
            await transaction.CommitAsync(Ct);
            clock.Now = clock.Now.AddSeconds(1);
        }
    }

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task AnEmptyLogIsIntact()
    {
        await _db.ExecuteAsync("DELETE FROM AuditEvents");

        var report = await VerifyAsync();

        Assert.True(report.IsIntact);
        Assert.Equal(0, report.VerifiedEvents);
        Assert.Null(report.Head);
        Assert.True(report.HeadHash.IsEmpty);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(1000)]
    public async Task AnUntouchedLogVerifiesEndToEnd(int batchSize)
    {
        var report = await VerifyAsync(batchSize);

        Assert.True(report.IsIntact, report.Break?.Description);
        Assert.Equal(Events, report.VerifiedEvents);
        Assert.Equal(_events[^1].Id, report.Head);
        Assert.Equal(_events[^1].Hash.ToArray(), report.HeadHash.ToArray());
    }

    public static TheoryData<string> FieldEdits =>
    [
        "Timestamp = '2026-11-07T12:00:02.0000001Z'",
        "ActorTokenId = randomblob(16)",
        "ActorKind = 'user', ActorUserId = ActorTokenId, ActorTokenId = NULL", // same ID bytes, now a user's
        "ActorKind = 'system', ActorTokenId = NULL",
        "RevisionId = NULL",
        "Channel = 'dashboard'",
        "Action = 'config.host.read'",
        "Target = NULL",
        "Outcome = 'denied'",
        "ClientIp = '203.0.113.9'",
        "Details = '{\"n\":99}'",
        "Details = '{\"n\": 2}'", // same JSON value, different text: still an edit
        "Hash = randomblob(32)",
    ];

    [Theory]
    [MemberData(nameof(FieldEdits))]
    public async Task AnEditedFieldIsReportedAtThatEvent(string assignments)
    {
        await _db.ExecuteAsync($"UPDATE AuditEvents SET {assignments} WHERE Id = {_events[2].Id.Value}");

        var report = await VerifyAsync(batchSize: 2);

        AssertBreak(report, _events[2].Id, AuditChainProblem.HashMismatch, verified: 2);
    }

    [Fact]
    public async Task AChangedIdIsAnEditOfThatEvent()
    {
        // The ID is in the hash: moving one event to a new ID (still between its neighbours) breaks its own hash.
        var moved = _events[2].Id.Value + 1;
        await _db.ExecuteAsync($"UPDATE AuditEvents SET Id = {moved} WHERE Id = {_events[2].Id.Value}");

        AssertBreak(await VerifyAsync(), AuditEventId.FromValue(moved), AuditChainProblem.HashMismatch, verified: 2);
    }

    [Fact]
    public async Task AnEditOfTheLastEventIsCaught()
    {
        await _db.ExecuteAsync($"UPDATE AuditEvents SET Outcome = 'failed' WHERE Id = {_events[^1].Id.Value}");

        AssertBreak(await VerifyAsync(), _events[^1].Id, AuditChainProblem.HashMismatch, verified: Events - 1);
    }

    [Fact]
    public async Task ADeletedEventIsReportedAtTheOneAfterIt()
    {
        await _db.ExecuteAsync($"DELETE FROM AuditEvents WHERE Id = {_events[3].Id.Value}");

        var report = await VerifyAsync(batchSize: 3); // the gap straddles a batch boundary

        AssertBreak(report, _events[4].Id, AuditChainProblem.BrokenLink, verified: 3);
        Assert.Contains(_events[2].Id.ToString(), report.Break!.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletingTheFirstEventsBreaksTheGenesis()
    {
        await _db.ExecuteAsync($"DELETE FROM AuditEvents WHERE Id <= {_events[1].Id.Value}");

        var report = await VerifyAsync();

        AssertBreak(report, _events[2].Id, AuditChainProblem.BrokenLink, verified: 0);
        Assert.Contains("first event", report.Break!.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReorderedEventsAreCaughtAtTheFirstMovedOne()
    {
        // Swap the IDs (= chain positions) of events 2 and 3.
        long a = _events[2].Id.Value, b = _events[3].Id.Value;
        await _db.ExecuteAsync($"UPDATE AuditEvents SET Id = -1 WHERE Id = {a}; UPDATE AuditEvents SET Id = {a} WHERE Id = {b}; UPDATE AuditEvents SET Id = {b} WHERE Id = -1");

        AssertBreak(await VerifyAsync(), _events[2].Id, AuditChainProblem.BrokenLink, verified: 2);
    }

    [Fact]
    public async Task AnInsertedEventIsCaught()
    {
        // A forged event between 2 and 3, hashed correctly over a made-up predecessor (it can't reuse event 3's
        // PrevHash: that one is taken, the index is unique).
        var forged = _events[2] with
        {
            Id = AuditEventId.FromValue(_events[2].Id.Value + 1),
            Action = "config.host.delete",
            PrevHash = Enumerable.Repeat((byte)7, 32).ToArray(),
        };
        forged = forged with { Hash = AuditChain.ComputeHash(forged) };
        await using (var context = _db.NewContext())
        {
            context.AuditEvents.Add(AuditMapping.ToEntity(forged));
            await context.SaveChangesAsync(Ct);
        }

        AssertBreak(await VerifyAsync(), forged.Id, AuditChainProblem.BrokenLink, verified: 3);
    }

    [Fact]
    public async Task ReHashingAnEditedEventMovesTheBreakToTheNextOne()
    {
        // Edit event 2 and recompute its own hash: it verifies on its own now, but event 3 still names the old hash.
        var edited = _events[2] with { Outcome = AuditOutcome.Denied };
        var hash = Convert.ToHexString(AuditChain.ComputeHash(edited));
        await _db.ExecuteAsync($"UPDATE AuditEvents SET Outcome = 'denied', Hash = x'{hash}' WHERE Id = {_events[2].Id.Value}");

        AssertBreak(await VerifyAsync(), _events[3].Id, AuditChainProblem.BrokenLink, verified: 3);
    }

    [Fact]
    public async Task AnUnreadableRowIsReportedInPlace()
    {
        // Damage the schema would refuse (check constraints bypassed): an unknown channel name, which EF's converter
        // can't read back.
        await _db.ExecuteUncheckedAsync($"UPDATE AuditEvents SET Channel = 'carrier-pigeon' WHERE Id = {_events[4].Id.Value}");

        AssertBreak(await VerifyAsync(batchSize: 4), _events[4].Id, AuditChainProblem.Unreadable, verified: 4);
        AssertBreak(await VerifyAsync(batchSize: 1000), _events[4].Id, AuditChainProblem.Unreadable, verified: 4);
    }

    [Fact]
    public async Task AWrongLengthIdBlobIsUnreadableToo()
    {
        // No check constraint involved: a typed-ID column holding 3 bytes instead of 16.
        await _db.ExecuteAsync($"UPDATE AuditEvents SET ActorTokenId = x'010203' WHERE Id = {_events[2].Id.Value}");

        AssertBreak(await VerifyAsync(batchSize: 2), _events[2].Id, AuditChainProblem.Unreadable, verified: 2);
    }

    [Fact]
    public async Task ErrorsThatAreNotDamagePropagate()
    {
        // A disposed context is a bug in the caller, not tampering: it must not be reported as an unreadable row.
        var context = _db.NewContext();
        await context.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => AuditChainVerifier.VerifyAsync(context, Ct));
    }

    [Fact]
    public async Task AnUnreadableRowAfterGoodOnesIsFoundInsideItsBatch()
    {
        await _db.ExecuteUncheckedAsync($"UPDATE AuditEvents SET Outcome = 'maybe' WHERE Id = {_events[5].Id.Value}");

        AssertBreak(await VerifyAsync(batchSize: 1000), _events[5].Id, AuditChainProblem.Unreadable, verified: 5);
    }

    [Fact]
    public async Task TruncatingTheNewestEventsShowsOnlyInTheHead()
    {
        // The documented limit of a hash chain: the remaining prefix is still a valid chain. An externally recorded
        // head (ID and hash) is what catches this.
        var before = await VerifyAsync();
        await _db.ExecuteAsync($"DELETE FROM AuditEvents WHERE Id = {_events[^1].Id.Value}");

        var after = await VerifyAsync();

        Assert.True(after.IsIntact);
        Assert.NotEqual(before.Head, after.Head);
        Assert.Equal(_events[^2].Id, after.Head);
    }

    [Fact]
    public async Task TheVerifierOnlyReads()
    {
        var capture = new SqlCapture();
        await using var context = _db.NewContext(capture);

        await AuditChainVerifier.VerifyAsync(context, Ct);

        Assert.All(capture.Queries, sql => Assert.StartsWith("SELECT", sql.TrimStart(), StringComparison.Ordinal));
        Assert.Equal(Events, (long)(await _db.ScalarAsync("SELECT count(*) FROM AuditEvents"))!);
    }

    private static void AssertBreak(AuditChainReport report, AuditEventId at, AuditChainProblem problem, int verified)
    {
        Assert.False(report.IsIntact);
        Assert.Equal(at, report.Break!.EventId);
        Assert.Equal(problem, report.Break.Problem);
        Assert.Equal(verified, report.VerifiedEvents);
    }

    private async Task<AuditChainReport> VerifyAsync(int batchSize = 1000)
    {
        await using var context = _db.NewContext();
        return await AuditChainVerifier.VerifyAsync(context, batchSize, Ct);
    }
}
