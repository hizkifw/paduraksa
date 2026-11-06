using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Ids;
using static Paduraksa.Tests.Data.SampleDatabase;

namespace Paduraksa.Tests.Data;

/// <summary>
/// <c>ORDER BY id</c> in SQLite (memcmp for BLOBs, numeric for INTEGER) agrees with <see cref="IComparable{T}"/>, with
/// ordinal string order and, for time-ordered kinds, with creation order.
/// </summary>
public sealed class TypedIdOrderingTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task UuidV7_SqlOrder_IsCreationOrder_IncludingWithinOneMillisecond()
    {
        var clock = new ManualTimeProvider(Start);
        var generator = new UuidV7Generator(clock);
        var created = new List<HostId>();
        for (var ms = 0; ms < 5; ms++)
        {
            clock.Now = Start.AddMilliseconds(ms);
            for (var i = 0; i < 100; i++) // 100 per millisecond: ordering comes from the counter, not the clock
            {
                created.Add(HostId.FromValue(generator.Next()));
            }
        }

        await using var db = await CreateAsync();
        await db.SaveAsync([.. Shuffled(created).Select(id => new Host { Id = id, Name = "h" })]);

        await AssertAllOrdersAgree(db, created, "Hosts", context => context.Hosts.OrderBy(h => h.Id).Select(h => h.Id));
    }

    [Fact]
    public async Task UuidV4_SqlOrder_MatchesCompareToAndStringOrder()
    {
        // Random IDs have no creation order, but BLOB order must still equal CompareTo and string order.
        var host = new Host { Id = HostId.New(), Name = "h" };
        var ids = Enumerable.Range(0, 300).Select(_ => ChallengeId.New()).ToList();
        await using var db = await CreateAsync();
        await db.SaveAsync([host, .. ids.Select(id => new Challenge { Id = id, HostId = host.Id })]);

        await AssertAllOrdersAgree(
            db, [.. ids.Order()], "Challenges", context => context.Challenges.OrderBy(c => c.Id).Select(c => c.Id));
    }

    [Fact]
    public async Task Int64Ordered_SqlOrder_IsCreationOrder_IncludingWithinOneMillisecond()
    {
        var clock = new ManualTimeProvider(Start);
        var generator = new Int64OrderedIdGenerator(clock);
        var created = new List<AuditEventId>();
        for (var ms = 0; ms < 5; ms++)
        {
            clock.Now = Start.AddMilliseconds(ms);
            for (var i = 0; i < 100; i++)
            {
                created.Add(AuditEventId.FromValue(generator.Next()));
            }
        }

        await using var db = await CreateAsync();
        await db.SaveAsync([.. Shuffled(created).Select(id => new AuditEvent { Id = id, Action = "a" })]);

        await AssertAllOrdersAgree(db, created, "AuditEvents", context => context.AuditEvents.OrderBy(a => a.Id).Select(a => a.Id));
    }

    [Fact]
    public async Task JsonIds_SqlStringOrder_MatchesCompareTo()
    {
        var clock = new ManualTimeProvider(Start);
        var generator = new UuidV7Generator(clock);
        var created = Enumerable.Range(0, 50).Select(_ => RouteId.FromValue(generator.Next())).ToList();
        await using var db = await CreateAsync();
        await db.SaveAsync([.. Shuffled(created).Select(id => new Host { Id = HostId.New(), Name = "h", Settings = { DefaultRouteId = id } })]);

        await using var context = db.NewContext();
        var viaEf = await context.Hosts.OrderBy(h => h.Settings.DefaultRouteId).Select(h => h.Settings.DefaultRouteId).ToListAsync(Ct);
        var viaSql = (await db.QueryAsync("SELECT Settings ->> '$.DefaultRouteId' FROM Hosts ORDER BY 1")).Select(r => RouteId.Parse((string)r[0]!));

        Assert.Equal(created, viaEf);
        Assert.Equal(created, viaSql);
    }

    /// <summary>
    /// <paramref name="expected"/> (creation order) = CompareTo order = ordinal string order = EF <c>OrderBy</c> =
    /// raw <c>ORDER BY Id</c>.
    /// </summary>
    private static async Task AssertAllOrdersAgree<TId>(
        SampleDatabase db, List<TId> expected, string table, Func<SampleDbContext, IQueryable<TId>> orderedByEf)
        where TId : struct, ITypedId<TId>
    {
        Assert.Equal(expected, expected.Order());
        Assert.Equal(expected, expected.OrderBy(id => id.ToString(), StringComparer.Ordinal));

        await using var context = db.NewContext();
        Assert.Equal(expected, await orderedByEf(context).ToListAsync(Ct));

        // Raw SQL, bypassing EF: compare the stored values in the database's own order.
        var stored = (await db.QueryAsync($"SELECT Id FROM {table} ORDER BY Id")).Select(r => r[0]).ToList();
        Assert.Equal(expected.Select(ToStored), stored);
    }

    private static object ToStored<TId>(TId id)
        where TId : struct, ITypedId<TId> => id switch
        {
            AuditEventId audit => audit.Value,
            HostId host => BigEndian(host.Value),
            ChallengeId challenge => BigEndian(challenge.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(id)),
        };

    private static byte[] BigEndian(Guid value)
    {
        var bytes = new byte[16];
        Assert.True(value.TryWriteBytes(bytes, bigEndian: true, out _));
        return bytes;
    }

    private static List<T> Shuffled<T>(List<T> items)
    {
        var copy = items.ToArray();
        new Random(42).Shuffle(copy);
        Assert.NotEqual(items, copy);
        return [.. copy];
    }
}
