using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Data.Audit;
using Paduraksa.Data.Revisions;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Config;

public sealed class ConfigModelStorageTests
{
    // Not UTC, with sub-millisecond ticks: both must survive.
    private static readonly DateTimeOffset Local = new DateTimeOffset(2026, 11, 7, 20, 34, 56, TimeSpan.FromHours(8)).AddTicks(7_890_123);

    [Fact]
    public async Task TimestampsAreFixedWidthUtcTextAndRoundTripToTheTick()
    {
        await using var db = await CreateAsync();
        var revision = Revision(1);
        revision.CreatedAt = Local;
        await SaveAsync(db, revision);

        var row = (await db.QueryAsync("SELECT typeof(CreatedAt), CreatedAt, datetime(CreatedAt) FROM ConfigRevisions")).Single();
        Assert.Equal("text", row[0]);
        Assert.Equal("2026-11-07T12:34:56.7890123Z", row[1]);
        Assert.Equal("2026-11-07 12:34:56", row[2]); // SQLite's date functions read it

        await using var context = db.NewContext();
        var read = (await context.ConfigRevisions.SingleAsync(Ct)).CreatedAt;
        Assert.Equal(Local.UtcTicks, read.UtcTicks);
        Assert.Equal(TimeSpan.Zero, read.Offset);
    }

    [Fact]
    public async Task TimestampComparisonsAndOrderingTranslateToSqlInTimeOrder()
    {
        await using var db = await CreateAsync();
        // Text order of EF's default format (with offsets) would put the +08:00 one first; instants say otherwise.
        var times = new[] { Local, Local.AddTicks(1).ToOffset(TimeSpan.FromHours(-5)), Local.AddDays(-1).ToOffset(TimeSpan.Zero) };
        await SaveAsync(db, [.. times.Select((t, i) => Event(i + 1, t))]);

        await using var context = db.NewContext();
        var after = await context.AuditEvents.Where(e => e.Timestamp > Local.AddHours(-1)).OrderByDescending(e => e.Timestamp).Select(e => e.Timestamp).ToListAsync(Ct);
        var latest = await context.AuditEvents.MaxAsync(e => e.Timestamp, Ct);

        Assert.Equal([times[1], times[0]], after);
        Assert.Equal(times[1], latest);
    }

    [Fact]
    public async Task EnumsAreStoredByTheirStableNames()
    {
        await using var db = await CreateAsync();
        var audit = Event(1, Local);
        audit.ActorKind = ActorKind.Token;
        audit.ActorTokenId = ApiTokenId.New();
        audit.Channel = Channel.Mcp;
        audit.Outcome = AuditOutcome.Conflict;
        await SaveAsync(db, audit);

        var row = (await db.QueryAsync("SELECT ActorKind, Channel, Outcome, typeof(ActorTokenId), length(ActorTokenId) FROM AuditEvents")).Single();

        Assert.Equal(new object?[] { "token", "mcp", "conflict", "blob", 16L }, row);
    }

    [Fact]
    public async Task KeysAreTypedIdColumnsAndTheAuditKeyIsTheRowid()
    {
        await using var db = await CreateAsync();
        var revision = Revision(1);
        var audit = Event(1, Local);
        audit.RevisionId = revision.Id;
        await SaveAsync(db, revision, audit);

        Assert.Equal(new object?[] { "blob", 16L }, (await db.QueryAsync("SELECT typeof(Id), length(Id) FROM ConfigRevisions")).Single());
        Assert.Equal(audit.Id.Value, await db.ScalarAsync("SELECT rowid FROM AuditEvents"));
        Assert.Equal("blob", await db.ScalarAsync("SELECT typeof(RevisionId) FROM AuditEvents"));
    }

    public static TheoryData<string> InvalidActors =>
    [
        "ActorKind = 'system', ActorUserId = randomblob(16)",
        "ActorKind = 'user'",
        "ActorKind = 'user', ActorTokenId = randomblob(16)",
        "ActorKind = 'token', ActorUserId = randomblob(16)",
        "ActorKind = 'robot'",
    ];

    [Theory]
    [MemberData(nameof(InvalidActors))]
    public async Task InconsistentActorColumnsAreRejected(string assignments)
    {
        await using var db = await CreateAsync();
        await SaveAsync(db, Revision(1), Event(1, Local));

        foreach (var table in new[] { "ConfigRevisions", "AuditEvents" })
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => db.ExecuteAsync($"UPDATE {table} SET {assignments}"));
            Assert.Contains("CHECK constraint failed: CK_", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task HashesMustBe32Bytes()
    {
        await using var db = await CreateAsync();
        await SaveAsync(db, Revision(1), Event(1, Local));

        foreach (var update in new[]
        {
            "UPDATE ConfigRevisions SET DocumentHash = randomblob(31)",
            "UPDATE AuditEvents SET PrevHash = randomblob(33)",
            "UPDATE AuditEvents SET Hash = x''",
        })
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => db.ExecuteAsync(update));
            Assert.Contains("CHECK constraint failed", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RevisionSeqAndParentAreUniqueSoHistoryCantFork()
    {
        await using var db = await CreateAsync();
        var first = Revision(1);
        var second = Revision(2, first.Id);
        await SaveAsync(db, first, second);

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, Revision(2, second.Id)));
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, Revision(3, first.Id)));
        await SaveAsync(db, Revision(3, second.Id));
    }

    [Fact]
    public async Task RevisionParentAndAuditRevisionMustExistAndCantBeDeleted()
    {
        await using var db = await CreateAsync();
        var root = Revision(1);
        await SaveAsync(db, root);
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, Revision(2, ConfigRevisionId.New())));
        var orphan = Event(1, Local);
        orphan.RevisionId = ConfigRevisionId.New();
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, orphan));

        var audit = Event(2, Local);
        audit.RevisionId = root.Id;
        await SaveAsync(db, audit);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => db.ExecuteAsync("DELETE FROM ConfigRevisions"));
        Assert.Contains("FOREIGN KEY", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyTheFirstRevisionIsARoot()
    {
        // A unique index treats NULLs as distinct, so the check constraint is what stops a second parentless revision.
        await using var db = await CreateAsync();
        var first = Revision(1);
        await SaveAsync(db, first);

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, Revision(2)));
        await using var other = await CreateAsync();
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(other, Revision(1, first.Id)));
    }

    public static TheoryData<string> UnknownNames =>
    [
        "UPDATE ConfigRevisions SET Channel = 'carrier-pigeon'",
        "UPDATE AuditEvents SET Channel = 'Api'",
        "UPDATE AuditEvents SET Outcome = 'maybe'",
    ];

    [Theory]
    [MemberData(nameof(UnknownNames))]
    public async Task EnumColumnsOnlyHoldKnownNames(string update)
    {
        await using var db = await CreateAsync();
        await SaveAsync(db, Revision(1), Event(1, Local));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => db.ExecuteAsync(update));

        Assert.Contains("CHECK constraint failed: CK_", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditPrevHashIsUniqueSoTheChainCantFork()
    {
        await using var db = await CreateAsync();
        await SaveAsync(db, Event(1, Local));

        var fork = Event(2, Local);
        fork.PrevHash = new byte[32]; // also claims to follow the genesis

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, fork));
    }

    private static async Task SaveAsync(TestConfigDatabase db, params object[] entities)
    {
        await using var context = db.NewContext();
        context.AddRange(entities);
        await context.SaveChangesAsync(Ct);
    }

    private static ConfigRevision Revision(long seq, ConfigRevisionId? parentId = null) => new()
    {
        Id = ConfigRevisionId.New(),
        Seq = seq,
        ParentId = parentId,
        ActorKind = ActorKind.System,
        Channel = Channel.System,
        CreatedAt = Local,
        Document = [1, 2, 3],
        DocumentHash = new byte[32],
        Summary = "[]",
    };

    private static AuditEvent Event(long id, DateTimeOffset timestamp) => new()
    {
        Id = AuditEventId.FromValue(id),
        Timestamp = timestamp,
        ActorKind = ActorKind.Cli,
        Channel = Channel.Cli,
        Action = "test.event",
        Outcome = AuditOutcome.Success,
        PrevHash = [.. Enumerable.Repeat((byte)(id - 1), 32)], // zeros for event 1
        Hash = [.. Enumerable.Repeat((byte)id, 32)],
    };
}
