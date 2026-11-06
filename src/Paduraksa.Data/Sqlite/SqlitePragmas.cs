using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SQLitePCL;

namespace Paduraksa.Data.Sqlite;

/// <summary>
/// The PLAN §5 settings that can't go in the connection string: <c>journal_mode=WAL</c>, <c>synchronous=NORMAL</c>
/// and <c>busy_timeout</c>. (<c>foreign_keys</c> and the retry timeout are connection-string keywords, see
/// <see cref="PaduraksaSqlite.ConnectionString"/>.)
/// </summary>
/// <remarks>
/// <c>synchronous</c> and <c>busy_timeout</c> belong to a physical connection and survive its trips through the
/// connection pool, so they're applied once per physical connection, not on every (logical) open. <c>journal_mode=WAL</c>
/// is stored in the database file; it rides along in the same batch, so the first connection to a new database
/// switches it to WAL without a separate startup step, and later ones find it already set (a no-op).
/// </remarks>
internal static class SqlitePragmas
{
    /// <summary>
    /// How long a statement waits for another connection's lock before failing with SQLITE_BUSY. One value for both
    /// SQLite's busy handler (<c>busy_timeout</c>) and Microsoft.Data.Sqlite's own retry loop (<c>Default Timeout</c>,
    /// otherwise 30 s). With a single writer, waits are short; 5 s absorbs a checkpoint or a long write while still
    /// failing fast enough to surface a stuck transaction.
    /// </summary>
    public static readonly TimeSpan BusyTimeout = TimeSpan.FromSeconds(5);

    public static readonly string Sql =
        $"PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = {(long)BusyTimeout.TotalMilliseconds};";

    // Physical connections (native handles) that already have the pragmas. Weak, so closed handles drop out.
    private static readonly ConditionalWeakTable<sqlite3, object> Applied = [];

    /// <summary>Applies the pragmas unless this physical connection already has them.</summary>
    public static async Task ApplyOnceAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var handle = Handle(connection);
        if (handle is not null && Applied.TryGetValue(handle, out _))
        {
            return;
        }

        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = Sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (handle is not null)
        {
            Applied.AddOrUpdate(handle, handle);
        }
    }

    /// <inheritdoc cref="ApplyOnceAsync"/>
    public static void ApplyOnce(DbConnection connection)
    {
        var handle = Handle(connection);
        if (handle is not null && Applied.TryGetValue(handle, out _))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.ExecuteNonQuery();
        if (handle is not null)
        {
            Applied.AddOrUpdate(handle, handle);
        }
    }

    private static sqlite3? Handle(DbConnection connection) => (connection as SqliteConnection)?.Handle;
}

/// <summary>Applies <see cref="SqlitePragmas"/> to each physical connection EF opens. Stateless; use <see cref="Instance"/>.</summary>
internal sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public static SqlitePragmaInterceptor Instance { get; } = new();

    private SqlitePragmaInterceptor()
    {
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        SqlitePragmas.ApplyOnce(connection);

    public override Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default) =>
        SqlitePragmas.ApplyOnceAsync(connection, cancellationToken);
}
