using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Paduraksa.Storage.Sqlite;

/// <summary>SQLite setup shared by every Paduraksa DbContext, on both databases (config and vault, PLAN §5).</summary>
public static class PaduraksaSqlite
{
    /// <summary>
    /// Connection string for a pooled connection to a database file (created if missing), with
    /// <c>Foreign Keys=True</c> and a <c>Default Timeout</c> equal to the busy timeout (5 s instead of
    /// Microsoft.Data.Sqlite's 30 s retry window).
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
    /// Uses SQLite with the PLAN §5 settings: the connection-string ones from <see cref="ConnectionString"/>, plus
    /// WAL, <c>synchronous=NORMAL</c> and <c>busy_timeout</c> applied once to every physical connection EF opens.
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
