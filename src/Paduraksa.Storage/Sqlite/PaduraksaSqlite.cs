using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Paduraksa.Storage.Sqlite;

/// <summary>SQLite setup shared by every Paduraksa DbContext, on both databases (config and vault).</summary>
public static class PaduraksaSqlite
{
    /// <summary>
    /// Connection string for a pooled connection to a database file (created if missing), with
    /// <c>Foreign Keys=True</c> and a <c>Default Timeout</c> equal to the busy timeout: 5 s rather than
    /// Microsoft.Data.Sqlite's 30 s retry window.
    /// </summary>
    public static string ConnectionString(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = (int)SqlitePragmas.BusyTimeout.TotalSeconds,
        }.ToString();
    }

    /// <summary>
    /// Connection string for a read-only look at an existing database file, safe beside a running server: it never creates the file, switches
    /// nothing to WAL and takes no write lock. Not pooled, so no handle outlives the caller.
    /// </summary>
    /// <remarks>
    /// Even a read-only connection to a WAL database creates its <c>-wal</c> and <c>-shm</c> files when they are missing, which would leave
    /// files behind in a data directory nothing was running on. So when there is no <c>-wal</c> file (no server holds the database open, and
    /// a clean close removes it) the file is opened <c>immutable</c>: SQLite then touches nothing but the main file. With a <c>-wal</c> file
    /// present it is opened normally, since the WAL may hold commits the main file lacks. A server that starts between that check and the
    /// open is not seen: its writes can make the immutable read torn. The look is read-only, so nothing is damaged, and it is meant for
    /// a data directory nothing is running on.
    /// </remarks>
    public static string ReadOnlyConnectionString(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var dataSource = databasePath;
        if (!File.Exists(databasePath + "-wal"))
        {
            dataSource = ImmutableUri(Path.GetFullPath(databasePath));
        }

        return new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = (int)SqlitePragmas.BusyTimeout.TotalSeconds,
        }.ToString();
    }

    /// <summary>The <c>file:</c> URI of a full path, opened immutable: each segment escaped, and a Windows drive's colon kept.</summary>
    internal static string ImmutableUri(string fullPath)
    {
        var path = fullPath.Replace(Path.DirectorySeparatorChar, '/');
        var drive = path.Length > 1 && path[1] == ':' && char.IsAsciiLetter(path[0]) ? "/" + path[..2] : "";
        return "file://" + drive + string.Join('/', path[(drive.Length > 0 ? 2 : 0)..].Split('/').Select(Uri.EscapeDataString)) + "?immutable=1";
    }

    /// <summary>
    /// Uses SQLite: the connection-string settings from <see cref="ConnectionString"/>, plus WAL,
    /// <c>synchronous=NORMAL</c> and <c>busy_timeout</c> applied once to every physical connection EF opens.
    /// </summary>
    public static DbContextOptionsBuilder UsePaduraksaSqlite(this DbContextOptionsBuilder optionsBuilder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return optionsBuilder
            .UseSqlite(connectionString)
            .AddInterceptors(SqlitePragmaInterceptor.Instance);
    }

    /// <summary>
    /// Registers a singleton <see cref="IDbContextFactory{TContext}"/> for the database at <paramref name="databasePath"/>.
    /// Reads create short-lived contexts from the factory (also what Blazor Server needs); writes go through the
    /// Application layer.
    /// </summary>
    public static IServiceCollection AddPaduraksaDbContextFactory<TContext>(this IServiceCollection services, string databasePath)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        var connectionString = ConnectionString(databasePath);
        return services.AddDbContextFactory<TContext>(options => options.UsePaduraksaSqlite(connectionString));
    }
}
