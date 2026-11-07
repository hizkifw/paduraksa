using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Paduraksa.Tests.Data.Sample;

/// <summary>
/// A real in-memory SQLite database that lives as long as this object: the connection is kept open (an in-memory
/// database disappears with its last connection) and shared by every context it creates.
/// </summary>
internal sealed class SampleDatabase : IAsyncDisposable
{
    private readonly DbContextOptions<SampleDbContext> _options;

    private SampleDatabase(SqliteConnection connection)
    {
        Connection = connection;
        _options = new DbContextOptionsBuilder<SampleDbContext>().UseSqlite(connection).Options;
    }

    public SqliteConnection Connection { get; }

    public static async Task<SampleDatabase> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(Ct);
        var database = new SampleDatabase(connection);
        await using var context = database.NewContext();
        await context.Database.EnsureCreatedAsync(Ct);
        return database;
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SampleDbContext NewContext() => new(_options);

    /// <summary>Saves the entities in a fresh context, so later reads can't be served from a change tracker.</summary>
    public async Task SaveAsync(params object[] entities)
    {
        await using var context = NewContext();
        context.AddRange(entities);
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>Runs raw SQL and returns every row as an array of column values.</summary>
    public async Task<List<object?[]>> QueryAsync(string sql)
    {
        await using var command = Connection.CreateCommand();
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

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
}
