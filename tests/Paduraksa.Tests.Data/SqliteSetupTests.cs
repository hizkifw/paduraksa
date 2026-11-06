using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paduraksa.Data.Sqlite;
using static Paduraksa.Tests.Data.SampleDatabase;

namespace Paduraksa.Tests.Data;

/// <summary>PLAN §5 connection settings on a real database file, through the registered context factory.</summary>
public sealed class SqliteSetupTests : IAsyncLifetime
{
    private readonly string _directory = Directory.CreateTempSubdirectory("paduraksa-data-").FullName;
    private ServiceProvider _services = null!;

    private string DatabasePath => Path.Combine(_directory, "paduraksa.db");

    private IDbContextFactory<SampleDbContext> Factory => _services.GetRequiredService<IDbContextFactory<SampleDbContext>>();

    public async ValueTask InitializeAsync()
    {
        _services = new ServiceCollection().AddPaduraksaDbContextFactory<SampleDbContext>(DatabasePath).BuildServiceProvider();
        await using var context = await Factory.CreateDbContextAsync(Ct);
        await context.Database.EnsureCreatedAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        SqliteConnection.ClearAllPools(); // pooled connections keep the file open
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task ContextFactory_OpensConnectionsWithThePlanSettings()
    {
        await using var context = await Factory.CreateDbContextAsync(Ct);
        await context.Database.OpenConnectionAsync(Ct);

        Assert.Equal("wal", await PragmaAsync(context, "journal_mode"));
        Assert.Equal(1L, await PragmaAsync(context, "synchronous")); // NORMAL
        Assert.Equal(1L, await PragmaAsync(context, "foreign_keys"));
        Assert.Equal((long)SqlitePragmas.BusyTimeout.TotalMilliseconds, await PragmaAsync(context, "busy_timeout"));
        Assert.Equal(SqlitePragmas.BusyTimeout.TotalSeconds, ((SqliteConnection)context.Database.GetDbConnection()).DefaultTimeout);
        Assert.Same(Factory, _services.GetRequiredService<IDbContextFactory<SampleDbContext>>()); // singleton
    }

    [Fact]
    public async Task Pragmas_RunOncePerPhysicalConnection_NotOnEveryOpen()
    {
        // Change a setting behind the interceptor's back; if the pragmas re-ran on the next (pooled) open, it would
        // be reset to NORMAL.
        await using (var first = await Factory.CreateDbContextAsync(Ct))
        {
            await first.Database.OpenConnectionAsync(Ct);
            await first.Database.ExecuteSqlRawAsync("PRAGMA synchronous = FULL", Ct);
        }

        await using var second = await Factory.CreateDbContextAsync(Ct);
        await second.Database.OpenConnectionAsync(Ct);
        Assert.Equal(2L, await PragmaAsync(second, "synchronous")); // FULL: same physical connection, pragmas not re-run
    }

    [Fact]
    public async Task WritesGoThroughTheWal()
    {
        var host = new Host { Id = HostId.New(), Name = "h" };
        await using (var write = await Factory.CreateDbContextAsync(Ct))
        {
            write.Add(host);
            await write.SaveChangesAsync(Ct);
        }

        Assert.True(File.Exists(DatabasePath + "-wal"));
        await using var read = await Factory.CreateDbContextAsync(Ct);
        Assert.Equal(host.Id, (await read.Hosts.SingleAsync(Ct)).Id);
    }

    [Fact]
    public async Task ForeignKeys_AreEnforcedBecauseOfTheConnectionString()
    {
        // The bundled SQLite may already default foreign_keys to on, so prove the setting itself: turned off
        // explicitly, the same orphan insert succeeds.
        var off = new SqliteConnectionStringBuilder(PaduraksaSqlite.ConnectionString(DatabasePath)) { ForeignKeys = false }.ToString();
        var insertOrphan = "INSERT INTO Routes (Id, HostId, \"Order\", RelatedHostIds) VALUES (randomblob(16), randomblob(16), 0, '[]')";

        await using (var withoutForeignKeys = new SqliteConnection(off))
        {
            await withoutForeignKeys.OpenAsync(Ct);
            await using var command = withoutForeignKeys.CreateCommand();
            command.CommandText = insertOrphan;
            Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
        }

        await using var context = await Factory.CreateDbContextAsync(Ct);
        var error = await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync(insertOrphan, Ct));
        Assert.Contains("FOREIGN KEY", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BusyWriter_FailsAfterTheBusyTimeout_NotMicrosoftDataSqlites30Seconds()
    {
        await using var holder = new SqliteConnection(PaduraksaSqlite.ConnectionString(DatabasePath));
        await holder.OpenAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct); // Microsoft.Data.Sqlite: BEGIN IMMEDIATE
        await using var context = await Factory.CreateDbContextAsync(Ct);
        context.Add(new Host { Id = HostId.New(), Name = "blocked" });

        var stopwatch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));

        Assert.Equal(5 /* SQLITE_BUSY */, Assert.IsType<SqliteException>(error.InnerException).SqliteErrorCode);
        Assert.InRange(stopwatch.Elapsed, SqlitePragmas.BusyTimeout * 0.8, SqlitePragmas.BusyTimeout * 3);
    }

    private static async Task<object?> PragmaAsync(DbContext context, string name)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA {name}";
        return await command.ExecuteScalarAsync(Ct);
    }
}
