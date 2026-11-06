using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Paduraksa.Core.Ids;
using Paduraksa.Data.Ids;
using static Paduraksa.Tests.Data.SampleDatabase;

namespace Paduraksa.Tests.Data;

public sealed class Int64OrderedIdSeedingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Observe_MaxPersistedId_KeepsNewIdsAboveIt_WhenTheClockIsBehind()
    {
        // IDs persisted by a previous run whose clock was an hour ahead of ours.
        var previousRun = new Int64OrderedIdGenerator(new ManualTimeProvider(Now.AddHours(1)));
        var persisted = Enumerable.Range(0, 5).Select(_ => AuditEventId.FromValue(previousRun.Next())).ToList();
        await using var db = await CreateAsync();
        await db.SaveAsync([.. persisted.Select(id => new AuditEvent { Id = id, Action = "a" })]);

        var generator = new Int64OrderedIdGenerator(new ManualTimeProvider(Now));
        Assert.True(generator.Next() < persisted[^1].Value); // the problem being solved
        await using var context = db.NewContext();
        var observed = await context.ObserveInt64OrderedKeysAsync(generator, Ct);

        Assert.Equal(new Dictionary<string, long> { ["AuditEvents"] = persisted[^1].Value }, observed);
        var next = AuditEventId.FromValue(generator.Next());
        Assert.True(next > persisted[^1]);
        await db.SaveAsync(new AuditEvent { Id = next, Action = "after restart" }); // no key collision
    }

    [Fact]
    public async Task Observe_EmptyTables_LeavesTheGeneratorAlone()
    {
        await using var db = await CreateAsync();
        var generator = new Int64OrderedIdGenerator(new ManualTimeProvider(Now));
        await using var context = db.NewContext();

        var observed = await context.ObserveInt64OrderedKeysAsync(generator, Ct);

        Assert.Empty(observed);
        Assert.Equal(Now, Int64OrderedIdGenerator.GetTimestamp(generator.Next()));
    }

    [Fact]
    public async Task Observe_OnlyLooksAtInt64OrderedPrimaryKeys()
    {
        // A nullable Int64Ordered column elsewhere (Routes.Origin_CreatedByAuditId) holds a larger value, but it isn't a
        // key this generator feeds, and Guid-keyed tables are skipped.
        await using var db = await CreateAsync();
        var key = AuditEventId.FromValue(1L << 30); // above any random counter seed (19 bits)
        var host = new Host { Id = HostId.New(), Name = "h" };
        await db.SaveAsync(
            new AuditEvent { Id = key, Action = "a" },
            host,
            new Route { Id = RouteId.New(), HostId = host.Id, Origin = { CreatedByAuditId = AuditEventId.FromValue(long.MaxValue) } });
        var generator = new Int64OrderedIdGenerator(new ManualTimeProvider(Int64OrderedIdGenerator.Epoch));
        await using var context = db.NewContext();

        var observed = await context.ObserveInt64OrderedKeysAsync(generator, Ct);

        Assert.Equal(["AuditEvents"], observed.Keys);
        Assert.Equal(key.Value + 1, generator.Next());
    }

    [Fact]
    public async Task Observe_TheSqlItRuns_IsAMaxOverTheRowidKey_NotAScan()
    {
        await using var db = await CreateAsync();
        var capture = new CommandCapture();
        var options = new DbContextOptionsBuilder<SampleDbContext>().UseSqlite(db.Connection).AddInterceptors(capture).Options;
        await using var context = new SampleDbContext(options);

        await context.ObserveInt64OrderedKeysAsync(new Int64OrderedIdGenerator(TimeProvider.System), Ct);

        var sql = Assert.Single(capture.Commands);
        Assert.Contains("MAX(\"Id\")", sql, StringComparison.Ordinal);
        var plan = string.Join("\n", (await db.QueryAsync("EXPLAIN QUERY PLAN " + sql)).Select(row => (string)row[3]!));
        // SQLite answers MAX over an INTEGER PRIMARY KEY with one B-tree probe; a scan would read the whole table.
        Assert.DoesNotContain("SCAN AuditEvents", plan, StringComparison.Ordinal);
        Assert.Contains("SEARCH AuditEvents", plan, StringComparison.Ordinal);
    }

    private sealed class CommandCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
