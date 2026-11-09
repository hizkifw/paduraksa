using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Paduraksa.Core.Ids;
using Paduraksa.Data;
using Paduraksa.Storage.Sqlite;

namespace Paduraksa.Tests.Data.Config;

/// <summary>
/// A real config database file in its own temp directory, opened through the registered context factory (pragmas
/// and all), deleted again on dispose.
/// </summary>
internal sealed class TestConfigDatabase : IAsyncDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("paduraksa-config-").FullName;
    private readonly ServiceProvider _services;

    /// <param name="sqliteRetryWindow">
    /// Pins Microsoft.Data.Sqlite's own retry window (<c>Default Timeout</c>) instead of deriving it from
    /// <see cref="SqlitePragmas.BusyTimeout"/>, for a test that has to tell that retry loop from the pragmas' busy handler.
    /// </param>
    private TestConfigDatabase(TimeSpan? sqliteRetryWindow = null)
    {
        ServiceCollection services = [];
        if (sqliteRetryWindow is { } window)
        {
            var connectionString = new SqliteConnectionStringBuilder(PaduraksaSqlite.ConnectionString(DatabasePath))
            {
                DefaultTimeout = (int)window.TotalSeconds,
            };
            services.AddDbContextFactory<ConfigDbContext>(options => options.UsePaduraksaSqlite(connectionString.ToString()));
        }
        else
        {
            services.AddPaduraksaDbContextFactory<ConfigDbContext>(DatabasePath);
        }

        _services = services.BuildServiceProvider();
        Factory = _services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public string DatabasePath => Path.Combine(_directory, ConfigDatabase.FileName);

    public IDbContextFactory<ConfigDbContext> Factory { get; }

    /// <summary>A database migrated to the current schema, the way startup does it.</summary>
    /// <param name="sqliteRetryWindow">See the constructor: pinned Microsoft.Data.Sqlite retry window, for one test.</param>
    public static async Task<TestConfigDatabase> CreateAsync(TimeSpan? sqliteRetryWindow = null)
    {
        var database = new TestConfigDatabase(sqliteRetryWindow);
        await ConfigDatabase.InitializeAsync(database.Factory, new Int64OrderedIdGenerator(TimeProvider.System), Ct);
        return database;
    }

    /// <summary>No database file yet.</summary>
    public static TestConfigDatabase Empty() => new();

    public ConfigDbContext NewContext() => Factory.CreateDbContext();

    /// <summary>A context outside the factory, with extra interceptors (e.g. to capture SQL).</summary>
    public ConfigDbContext NewContext(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<ConfigDbContext>();
        options.UsePaduraksaSqlite(PaduraksaSqlite.ConnectionString(DatabasePath)).AddInterceptors(interceptors);
        return new ConfigDbContext(options.Options);
    }

    /// <summary>Runs raw SQL on a separate connection (no EF, no change tracker) and returns every row.</summary>
    public async Task<List<object?[]>> QueryAsync(string sql)
    {
        await using var connection = new SqliteConnection(PaduraksaSqlite.ConnectionString(DatabasePath));
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(Ct))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    public async Task<object?> ScalarAsync(string sql) => (await QueryAsync(sql)).Single()[0];

    /// <summary>Runs a raw SQL statement (tampering, constraint probes) and returns the affected row count.</summary>
    public async Task<int> ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(PaduraksaSqlite.ConnectionString(DatabasePath));
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>
    /// <see cref="ExecuteAsync"/> with check constraints off, to plant rows the schema would refuse (damage done
    /// outside SQLite's rules). Uses an unpooled connection, so the setting never leaks into other commands.
    /// </summary>
    public async Task<int> ExecuteUncheckedAsync(string sql)
    {
        var unpooled = new SqliteConnectionStringBuilder(PaduraksaSqlite.ConnectionString(DatabasePath)) { Pooling = false };
        await using var connection = new SqliteConnection(unpooled.ToString());
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA ignore_check_constraints = 1; {sql}";
        return await command.ExecuteNonQueryAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        // Pooled connections keep the file open. Clear only this database's pool: ClearAllPools would also close
        // connections that tests running in parallel rely on (SqliteSetupTests checks physical-connection reuse).
        using (var connection = new SqliteConnection(PaduraksaSqlite.ConnectionString(DatabasePath)))
        {
            SqliteConnection.ClearPool(connection);
        }

        Directory.Delete(_directory, recursive: true);
    }
}
