using Microsoft.EntityFrameworkCore;
using Npgsql;
using Paduraksa.Tests.TypedIdSample;
using Paduraksa.TypedIds.EntityFrameworkCore;
using static Paduraksa.Tests.TypedIdSample.SampleDatabase;

namespace Paduraksa.TypedIds.Tests;

/// <summary>
/// The native Guid columns against a real PostgreSQL: a UUIDv7 key must come back equal and keep creation order in
/// <c>ORDER BY</c> and in range comparisons, which is what makes <c>uuid</c> worth choosing there.
/// </summary>
public sealed class PostgresRoundTripTests(PostgresContainer postgres) : IClassFixture<PostgresContainer>
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task UuidV7KeysAreUuidColumnsThatKeepCreationOrderAndRoundTrip()
    {
        Requirements.RequireAvailable(Requirements.Postgres, postgres.SkipReason);
        await using var database = await PostgresDatabase.CreateAsync(postgres.ConnectionString);
        var clock = new ManualTimeProvider(Start);
        var generator = new UuidV7Generator(clock);
        var created = new List<HostId>();
        for (var ms = 0; ms < 5; ms++)
        {
            clock.Now = Start.AddMilliseconds(ms);
            for (var i = 0; i < 40; i++) // several per millisecond: the order comes from the counter there
            {
                created.Add(HostId.FromValue(generator.Next()));
            }
        }

        await using (var write = database.NewContext())
        {
            write.AddRange(created.OrderBy(_ => Random.Shared.Next()).Select(id => new Host { Id = id, Name = "h" }));
            await write.SaveChangesAsync(Ct);
        }

        await using var read = database.NewContext();
        Assert.Equal("uuid", await database.ScalarAsync("SELECT data_type FROM information_schema.columns WHERE table_name = 'Host' AND column_name = 'Id'"));
        Assert.Equal(created, await read.Set<Host>().OrderBy(h => h.Id).Select(h => h.Id).ToListAsync(Ct));
        Assert.Equal(created.Count, await read.Set<Host>().CountAsync(Ct));

        var pivot = created[created.Count / 2];
        Assert.Equal(created.Where(id => id > pivot), await read.Set<Host>().Where(h => h.Id > pivot).OrderBy(h => h.Id).Select(h => h.Id).ToListAsync(Ct));
        Assert.Equal(pivot, (await read.Set<Host>().SingleAsync(h => h.Id == pivot, Ct)).Id);
    }

    [Fact]
    public async Task ForeignKeysAndJsonDocumentsRoundTrip()
    {
        Requirements.RequireAvailable(Requirements.Postgres, postgres.SkipReason);
        await using var database = await PostgresDatabase.CreateAsync(postgres.ConnectionString);
        var parent = new Host { Id = HostId.New(), Name = "parent" };
        var route = RouteId.New();
        var audit = AuditEventId.New();
        var child = new Host
        {
            Id = HostId.New(),
            Name = "child",
            ParentId = parent.Id,
            Settings = { DefaultRouteId = route, LastAuditId = audit, AllowedRouteIds = [route], Aliases = [new HostAlias { TargetHostId = parent.Id }] },
        };
        var challenge = new Challenge { Id = ChallengeId.New(), HostId = child.Id };
        await using (var write = database.NewContext())
        {
            write.AddRange(parent, child, challenge, new AuditEvent { Id = audit, Action = "a" });
            await write.SaveChangesAsync(Ct);
        }

        await using var read = database.NewContext();
        var loaded = await read.Set<Host>().SingleAsync(h => h.ParentId == parent.Id, Ct);
        Assert.Equal(child.Id, loaded.Id);
        Assert.Equal((route, audit, parent.Id), (loaded.Settings.DefaultRouteId, loaded.Settings.LastAuditId!.Value, loaded.Settings.Aliases.Single().TargetHostId));
        Assert.Equal([route], loaded.Settings.AllowedRouteIds);
        Assert.Equal(challenge.Id, (await read.Set<Challenge>().SingleAsync(c => c.HostId == child.Id, Ct)).Id);
        Assert.Equal(audit, (await read.Set<AuditEvent>().SingleAsync(Ct)).Id);
        // Inside the document an ID is its canonical string, as on every provider.
        Assert.Equal(route.ToString(), await database.ScalarAsync("SELECT \"Settings\" ->> 'DefaultRouteId' FROM \"Host\" WHERE \"Name\" = 'child'"));
    }

    private sealed class PostgresDatabase : IAsyncDisposable
    {
        private readonly DbContextOptions<NativeGuidContext> _options;
        private readonly string _admin;
        private readonly string _name;

        private PostgresDatabase(string admin, string name, string connectionString)
        {
            _admin = admin;
            _name = name;
            ConnectionString = connectionString;
            _options = new DbContextOptionsBuilder<NativeGuidContext>().UseNpgsql(connectionString).Options;
        }

        public string ConnectionString { get; }

        /// <summary>A database of its own per test, so tests don't see each other's rows. The name is mixed-case base62, so it is quoted: unquoted, PostgreSQL folds it to lowercase.</summary>
        public static async Task<PostgresDatabase> CreateAsync(string admin)
        {
            var name = "t" + HostId.New().ToString().Replace("_", "", StringComparison.Ordinal);
            await ExecuteAsync(admin, $"CREATE DATABASE \"{name}\"");
            var connectionString = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
            PostgresDatabase database = new(admin, name, connectionString);
            await using var context = database.NewContext();
            await context.Database.EnsureCreatedAsync(Ct);
            return database;
        }

        public NativeGuidContext NewContext() => new(_options);

        public async Task<object?> ScalarAsync(string sql)
        {
            await using NpgsqlConnection connection = new(ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            return await command.ExecuteScalarAsync(Ct);
        }

        public async ValueTask DisposeAsync() => await ExecuteAsync(_admin, $"DROP DATABASE \"{_name}\" WITH (FORCE)");

        private static async Task ExecuteAsync(string connectionString, string sql)
        {
            await using NpgsqlConnection connection = new(connectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(Ct);
        }
    }
}
