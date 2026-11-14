using Microsoft.Data.Sqlite;

namespace Paduraksa.Vault.Backup;

/// <summary>The few things the backup does to a SQLite file directly, over unpooled connections so nothing keeps a file open once it is done with.</summary>
internal static class SqliteFiles
{
    private static readonly byte[] DatabaseMagic = "SQLite format 3\0"u8.ToArray();

    /// <summary>
    /// How long a snapshot waits for a lock it needs on a live database. Generous because a backup is deliberate and nobody is behind it;
    /// the waits it covers (a writer rebuilding the log's index after the last connection closed, a checkpoint) last milliseconds.
    /// </summary>
    private const int SnapshotBusyMilliseconds = 30_000;

    /// <summary>
    /// A consistent copy of <paramref name="sourcePath"/> made with SQLite's online backup API, while others read and write it: the
    /// whole database is read in one step inside one read transaction, so the copy is the state at one instant, whatever the write-ahead
    /// log holds. (A file copy of a live WAL database can miss pages that are only in the log, or tear.)
    /// </summary>
    public static void Snapshot(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"The database {sourcePath} does not exist.", sourcePath);
        }

        using var source = new SqliteConnection(Connection(sourcePath, SqliteOpenMode.ReadOnly));
        using var destination = new SqliteConnection(Connection(destinationPath, SqliteOpenMode.ReadWriteCreate));
        source.Open();
        destination.Open();
        using (var timeout = source.CreateCommand())
        {
            timeout.CommandText = $"PRAGMA busy_timeout = {SnapshotBusyMilliseconds}";
            timeout.ExecuteNonQuery();
        }

        source.BackupDatabase(destination);
    }

    /// <summary>The EF migration IDs recorded in the database at <paramref name="path"/>, oldest first (none for a database EF never touched).</summary>
    public static IReadOnlyList<string> Migrations(string path)
    {
        using var connection = new SqliteConnection(Connection(path, SqliteOpenMode.ReadOnly));
        connection.Open();
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory'";
        if (Convert.ToInt64(exists.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId";
        using var reader = command.ExecuteReader();
        List<string> ids = [];
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <summary>Whether the file starts like a SQLite database and passes SQLite's own quick integrity check.</summary>
    public static bool IsSound(string path)
    {
        try
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Span<byte> head = stackalloc byte[16];
                if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length || !head.SequenceEqual(DatabaseMagic))
                {
                    return false;
                }
            }

            using var connection = new SqliteConnection(Connection(path, SqliteOpenMode.ReadOnly));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check";
            return string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.Ordinal);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>
    /// A compact copy of <paramref name="sourcePath"/> in <paramref name="destinationPath"/> holding only live content: pages freed by
    /// updates (the old wrapped keys of a re-wrap) are not carried over. The destination is created first with mode 0600, so it is never
    /// readable by others, not even briefly.
    /// </summary>
    public static void CompactCopy(string sourcePath, string destinationPath)
    {
        CreateEmptyPrivateFile(destinationPath);
        using var connection = new SqliteConnection(Connection(sourcePath, SqliteOpenMode.ReadWrite));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $path";
        command.Parameters.AddWithValue("$path", destinationPath);
        command.ExecuteNonQuery();
    }

    public static string Connection(string path, SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false, ForeignKeys = true }.ToString();

    /// <summary>An empty file, which SQLite treats as a new database, created owner-only from the first byte.</summary>
    public static void CreateEmptyPrivateFile(string path)
    {
        FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        new FileStream(path, options).Dispose();
    }

    /// <summary>Creates <paramref name="path"/> (and its missing parents) readable by the owner only.</summary>
    public static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
