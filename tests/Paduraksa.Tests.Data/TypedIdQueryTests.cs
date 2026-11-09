using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Ids;
using Paduraksa.Tests.Data.Sample;
using static Paduraksa.Tests.Data.Sample.SampleDatabase;

namespace Paduraksa.Tests.Data;

/// <summary>
/// LINQ over typed IDs translates to SQL on the stored values (never client-side evaluation, which EF would reject
/// with an exception), and gives the same answers as the in-memory operators.
/// </summary>
public sealed class TypedIdQueryTests : IAsyncLifetime
{
    private SampleDatabase _db = null!;
    private List<Host> _hosts = null!;
    private List<AuditEvent> _events = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await CreateAsync();
        _hosts = [.. Enumerable.Range(0, 10).Select(i => new Host { Id = HostId.New(), Name = $"h{i}", Settings = { DefaultRouteId = RouteId.New() } })];
        _events = [.. Enumerable.Range(0, 10).Select(i => new AuditEvent { Id = AuditEventId.New(), Action = $"a{i}" })];
        var challenge = new Challenge { Id = ChallengeId.New(), HostId = _hosts[0].Id };
        var routes = new[]
        {
            new Route { Id = RouteId.New(), HostId = _hosts[0].Id, ChallengeId = challenge.Id },
            new Route { Id = RouteId.New(), HostId = _hosts[0].Id },
        };
        await _db.SaveAsync([.. _hosts, .. _events, challenge, .. routes]);
    }

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task EqualityTranslatesToAParameterComparison()
    {
        await using var context = _db.NewContext();
        var id = _hosts[3].Id;
        var query = context.Hosts.Where(h => h.Id == id);

        AssertSql(query, "WHERE \"h\".\"Id\" = @id");
        Assert.Equal("h3", (await query.SingleAsync(Ct)).Name);
        Assert.Equal("h3", (await context.Hosts.SingleAsync(h => h.Id == HostId.Parse(id.ToString()), Ct)).Name);
    }

    [Fact]
    public async Task InequalityTranslatesToNotEqual()
    {
        await using var context = _db.NewContext();
        var id = _hosts[3].Id;
        var query = context.Hosts.Where(h => h.Id != id);

        AssertSql(query, "WHERE \"h\".\"Id\" <> @id");
        Assert.Equal(9, await query.CountAsync(Ct));
    }

    [Fact]
    public async Task NullableEqualityAndNullChecksTranslate()
    {
        await using var context = _db.NewContext();
        ChallengeId? challengeId = (await context.Challenges.SingleAsync(Ct)).Id;
        var withChallenge = context.Routes.Where(r => r.ChallengeId == challengeId);
        var withoutChallenge = context.Routes.Where(r => r.ChallengeId == null);

        AssertSql(withChallenge, "WHERE \"r\".\"ChallengeId\" = @challengeId");
        AssertSql(withoutChallenge, "WHERE \"r\".\"ChallengeId\" IS NULL");
        Assert.Equal(1, await withChallenge.CountAsync(Ct));
        Assert.Equal(1, await withoutChallenge.CountAsync(Ct));
    }

    [Fact]
    public async Task ContainsOverAListOfIdsTranslatesToIn()
    {
        await using var context = _db.NewContext();
        List<HostId> ids = [_hosts[1].Id, _hosts[4].Id, _hosts[7].Id, HostId.New()];
        AuditEventId[] eventIds = [_events[2].Id, _events[5].Id];
        var hosts = context.Hosts.Where(h => ids.Contains(h.Id));
        var events = context.AuditEvents.Where(a => eventIds.Contains(a.Id));

        AssertSql(hosts, "WHERE \"h\".\"Id\" IN (@ids1, @ids2, @ids3, @ids4)");
        AssertSql(events, "WHERE \"a\".\"Id\" IN (@eventIds1, @eventIds2)");
        Assert.Equal(["h1", "h4", "h7"], await hosts.OrderBy(h => h.Id).Select(h => h.Name).ToListAsync(Ct));
        Assert.Equal(["a2", "a5"], await events.OrderBy(a => a.Id).Select(a => a.Action).ToListAsync(Ct));
    }

    [Fact]
    public async Task OrderByTranslatesToOrderByTheColumn()
    {
        await using var context = _db.NewContext();
        var ascending = context.Hosts.OrderBy(h => h.Id).Select(h => h.Id);
        var descending = context.AuditEvents.OrderByDescending(a => a.Id).Select(a => a.Id);

        AssertSql(ascending, "ORDER BY \"h\".\"Id\"");
        AssertSql(descending, "ORDER BY \"a\".\"Id\" DESC");
        Assert.Equal(_hosts.Select(h => h.Id), await ascending.ToListAsync(Ct));
        Assert.Equal(_events.Select(a => a.Id).Reverse(), await descending.ToListAsync(Ct));
    }

    [Fact]
    public async Task ComparisonOperatorsTranslateToSqlComparisons()
    {
        await using var context = _db.NewContext();
        var cursor = _hosts[4].Id;
        var auditCursor = _events[6].Id;

        AssertSql(context.Hosts.Where(h => h.Id < cursor), "WHERE \"h\".\"Id\" < @cursor");
        AssertSql(context.Hosts.Where(h => h.Id <= cursor), "WHERE \"h\".\"Id\" <= @cursor");
        AssertSql(context.Hosts.Where(h => h.Id > cursor), "WHERE \"h\".\"Id\" > @cursor");
        AssertSql(context.Hosts.Where(h => h.Id >= cursor), "WHERE \"h\".\"Id\" >= @cursor");
        AssertSql(context.AuditEvents.Where(a => a.Id > auditCursor), "WHERE \"a\".\"Id\" > @auditCursor");

        Assert.Equal(4, await context.Hosts.CountAsync(h => h.Id < cursor, Ct));
        Assert.Equal(5, await context.Hosts.CountAsync(h => h.Id <= cursor, Ct));
        Assert.Equal(5, await context.Hosts.CountAsync(h => h.Id > cursor, Ct));
        Assert.Equal(6, await context.Hosts.CountAsync(h => h.Id >= cursor, Ct));
        Assert.Equal(3, await context.AuditEvents.CountAsync(a => a.Id > auditCursor, Ct));
    }

    [Fact]
    public async Task CompareToTranslatesLikeTheOperators()
    {
        await using var context = _db.NewContext();
        var cursor = _hosts[4].Id;
        var query = context.Hosts.Where(h => h.Id.CompareTo(cursor) > 0);

        AssertSql(query, "WHERE \"h\".\"Id\" > @cursor");
        Assert.Equal(5, await query.CountAsync(Ct));
    }

    [Fact]
    public async Task CursorPaginationWalksEveryRowOnceInIdOrder()
    {
        await using var context = _db.NewContext();
        var forward = await Paginate(context.Hosts, h => h.Id, pageSize: 3, descending: false);
        var backward = await Paginate(context.AuditEvents, a => a.Id, pageSize: 4, descending: true);

        Assert.Equal(_hosts.Select(h => h.Id), forward);
        Assert.Equal(_events.Select(a => a.Id).Reverse(), backward);
    }

    [Fact]
    public async Task JsonMembersCompareAsCanonicalStrings()
    {
        await using var context = _db.NewContext();
        var routeId = _hosts[5].Settings.DefaultRouteId;
        var equal = context.Hosts.Where(h => h.Settings.DefaultRouteId == routeId);
        var after = context.Hosts.Where(h => h.Settings.DefaultRouteId > routeId);

        AssertSql(equal, "WHERE \"h\".\"Settings\" ->> 'DefaultRouteId' = @routeId");
        Assert.Contains($"'{routeId}'", equal.ToQueryString(), StringComparison.Ordinal); // a string parameter, not a BLOB
        Assert.Equal("h5", (await equal.SingleAsync(Ct)).Name);
        Assert.Equal(["h6", "h7", "h8", "h9"], await after.OrderBy(h => h.Settings.DefaultRouteId).Select(h => h.Name).ToListAsync(Ct));
    }

    [Fact]
    public async Task JsonIdsNeverEqualColumnIdsInSql()
    {
        // Hard rule: an ID inside a JSON document is TEXT, a column ID is a BLOB/INTEGER, so SQL never
        // finds them equal. References inside JSON settings are resolved in memory by the snapshot compiler, never
        // joined in SQL. This pins the behaviour so a change is noticed.
        var host = _hosts[2];
        await using (var update = _db.NewContext())
        {
            var tracked = await update.Hosts.SingleAsync(h => h.Id == host.Id, Ct);
            tracked.Settings.FallbackHostId = tracked.Id;
            update.Add(new Route { Id = tracked.Settings.DefaultRouteId, HostId = tracked.Id });
            await update.SaveChangesAsync(Ct);
        }

        await using var context = _db.NewContext();
        var inMemory = await context.Hosts.ToListAsync(Ct);
        Assert.Single(inMemory, h => h.Settings.FallbackHostId == h.Id);

        Assert.Equal(0, await context.Hosts.CountAsync(h => h.Settings.FallbackHostId == h.Id, Ct));
        Assert.Equal(0, await context.Hosts.CountAsync(h => context.Routes.Any(r => r.Id == h.Settings.DefaultRouteId), Ct));
        Assert.Empty(await (
            from h in context.Hosts
            join r in context.Routes on h.Settings.DefaultRouteId equals r.Id
            select r.Id).ToListAsync(Ct));
    }

    [Fact]
    public async Task ValueMemberAccessIsNotTranslatedSoUseTheIdItself()
    {
        // EF can't see through a value converter, so `.Value` is client-side and EF refuses it. Compare IDs directly
        // (the operators above translate); this test pins that behaviour so a future EF change is noticed.
        await using var context = _db.NewContext();
        var cursor = _events[3].Id.Value;

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.AuditEvents.Where(a => a.Id.Value > cursor).ToListAsync(Ct));
    }

    private static async Task<List<TId>> Paginate<TEntity, TId>(
        IQueryable<TEntity> source, Expression<Func<TEntity, TId>> key, int pageSize, bool descending)
        where TEntity : class
        where TId : struct, ITypedId<TId>
    {
        var all = new List<TId>();
        TId? cursor = null;
        var keyOf = key.Compile();
        while (true)
        {
            var page = source;
            if (cursor is { } after)
            {
                page = page.Where(Compare(key, after, descending));
            }

            page = descending ? page.OrderByDescending(key) : page.OrderBy(key);
            var rows = await page.Take(pageSize).ToListAsync(Ct);
            all.AddRange(rows.Select(keyOf));
            if (rows.Count < pageSize)
            {
                return all;
            }

            cursor = keyOf(rows[^1]);
        }
    }

    /// <summary>Builds <c>e =&gt; key(e) &gt; after</c> (or <c>&lt;</c>), as a parameter like a captured variable.</summary>
    private static Expression<Func<TEntity, bool>> Compare<TEntity, TId>(
        Expression<Func<TEntity, TId>> key, TId after, bool descending)
        where TId : struct, ITypedId<TId>
    {
        var boxed = new StrongBox<TId>(after);
        var value = Expression.Field(Expression.Constant(boxed), nameof(StrongBox<TId>.Value));
        var body = descending
            ? Expression.LessThan(key.Body, value)
            : Expression.GreaterThan(key.Body, value);
        return Expression.Lambda<Func<TEntity, bool>>(body, key.Parameters);
    }

    private static void AssertSql<T>(IQueryable<T> query, string expected) =>
        Assert.Contains(expected, query.ToQueryString(), StringComparison.Ordinal);
}
