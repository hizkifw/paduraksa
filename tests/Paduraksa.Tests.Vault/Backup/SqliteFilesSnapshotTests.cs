using Microsoft.Data.Sqlite;
using Paduraksa.Tests.Vault.Testing;
using Paduraksa.Vault.Backup;

namespace Paduraksa.Tests.Vault.Backup;

public sealed class SqliteFilesSnapshotTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    /// <summary>
    /// The server's connection pool closes idle connections, and when the last one closes SQLite checkpoints and deletes the write-ahead
    /// log; the next connection to open rebuilds the log's index. A snapshot that opens in between meets a held recovery lock, which is
    /// SQLITE_BUSY at once unless the snapshot's own connection waits. The writer here opens and closes an unpooled connection per
    /// write to make that window the common case (the pool makes it rare, which is why a full CI-like run failed about one time in five).
    /// </summary>
    [Fact]
    public async Task ASnapshotWaitsForAWriterThatOpensAndClosesTheLastConnection()
    {
        var source = _directory.File("source.db");
        var connectionString = SqliteFiles.Connection(source, SqliteOpenMode.ReadWriteCreate);
        using (var setup = new SqliteConnection(connectionString))
        {
            setup.Open();
            Execute(setup, "PRAGMA journal_mode = WAL; CREATE TABLE Notes (Id INTEGER PRIMARY KEY, Body BLOB); INSERT INTO Notes VALUES (1, randomblob(1000));");
        }

        using var stop = new CancellationTokenSource();
        var writer = Task.Factory.StartNew(
            () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    using var connection = new SqliteConnection(connectionString);
                    connection.Open();
                    Execute(connection, "PRAGMA busy_timeout = 5000; UPDATE Notes SET Body = randomblob(2000) WHERE Id = 1");
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        // Each snapshot takes well under a millisecond and the unfixed code misses within the first hundred or so, so this many make a pass mean something.
        for (var i = 0; i < 2000; i++)
        {
            var destination = _directory.File($"copy-{i}.db");
            SqliteFiles.Snapshot(source, destination);
            File.Delete(destination);
        }

        await stop.CancelAsync();
        await writer;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
