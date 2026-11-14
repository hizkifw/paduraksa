using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Ids;
using Paduraksa.Storage.Ids;
using Paduraksa.Storage.Sqlite;

namespace Paduraksa.Data;

public static class ConfigDatabase
{
    /// <summary>The database file name inside the data directory.</summary>
    public const string FileName = "paduraksa.db";

    /// <summary>
    /// Brings the database up to date and makes it safe to create IDs: applies pending migrations (creating the file
    /// if needed), then seeds <paramref name="generator"/> from every Int64Ordered table
    /// (<see cref="Int64OrderedIdSeeding"/>). Run once at startup, before anything reads or writes config. The generator
    /// is normally <see cref="Int64OrderedIdGenerator.Shared"/>; the result is for startup logging. The file is created 0600, and an
    /// existing one (with its <c>-wal</c> and <c>-shm</c>) that others can read is restricted to 0600 first, because it holds the
    /// users' password hashes, the session and token hashes and the audit log: <see cref="ConfigDatabaseInitialized.RestrictedFiles"/> names them.
    /// </summary>
    public static async Task<ConfigDatabaseInitialized> InitializeAsync(
        IDbContextFactory<ConfigDbContext> factory, Int64OrderedIdGenerator generator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(generator);

        var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var databasePath = context.Database.GetDbConnection().DataSource;
            PrivateSqliteFile.EnsureCreated(databasePath);
            var restricted = PrivateSqliteFile.Restrict(databasePath);
            var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            var observed = await context.ObserveInt64OrderedKeysAsync(generator, cancellationToken).ConfigureAwait(false);
            return new ConfigDatabaseInitialized(pending, observed) { RestrictedFiles = restricted.Changed, UnrestrictedFiles = [.. restricted.Failed.Select(f => new UnrestrictedFile(f.Path, f.Mode, f.Reason))] };
        }
    }

    /// <summary>
    /// Opens the database at <paramref name="databasePath"/> read-only: nothing is created, migrated, restricted or written, so this is safe beside a
    /// running server. The file must exist.
    /// </summary>
    public static ConfigDbContext OpenReadOnly(string databasePath)
    {
        var builder = new DbContextOptionsBuilder<ConfigDbContext>();
        builder.UsePaduraksaSqlite(PaduraksaSqlite.ReadOnlyConnectionString(databasePath));
        return new ConfigDbContext(builder.Options);
    }

    /// <summary>
    /// Reads the schema state without changing anything (<c>paduraksa doctor</c>): what the database has applied against this build's migrations.
    /// A file that can't be opened or read is a <see cref="ConfigDatabaseInspection.Problem"/>, not an exception.
    /// </summary>
    public static async Task<ConfigDatabaseInspection> InspectAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!File.Exists(databasePath))
        {
            return new ConfigDatabaseInspection(false, new MigrationState([], [], []), null);
        }

        try
        {
            var context = OpenReadOnly(databasePath);
            await using (context.ConfigureAwait(false))
            {
                return new ConfigDatabaseInspection(true, await MigrationInspector.InspectAsync(context, cancellationToken).ConfigureAwait(false), null);
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new ConfigDatabaseInspection(true, new MigrationState([], [], []), ex.Message);
        }
    }
}

/// <summary>What <see cref="ConfigDatabase.InspectAsync"/> found.</summary>
/// <param name="Exists">Whether the file is there (a missing one is created by the next start).</param>
/// <param name="Migrations">The schema against this build's migrations; empty when the file is missing.</param>
/// <param name="Problem">Why it could not be opened or read, or null.</param>
public sealed record ConfigDatabaseInspection(bool Exists, MigrationState Migrations, string? Problem);

/// <summary>The outcome of <see cref="ConfigDatabase.InitializeAsync"/>.</summary>
/// <param name="AppliedMigrations">Migrations applied by this start, oldest first (empty if the schema was current).</param>
/// <param name="ObservedKeys">The largest Int64Ordered key per non-empty table, as passed to the generator.</param>
public sealed record ConfigDatabaseInitialized(
    IReadOnlyList<string> AppliedMigrations, IReadOnlyDictionary<string, long> ObservedKeys)
{
    /// <summary>The database files that others could read and that startup restricted to their owner (an install from before they were created 0600).</summary>
    public IReadOnlyList<string> RestrictedFiles { get; init; } = [];

    /// <summary>The database files that others can read and that startup could not restrict (another user's file, a read-only mount), with why.</summary>
    public IReadOnlyList<UnrestrictedFile> UnrestrictedFiles { get; init; } = [];
}

/// <summary>A database file that others can read and that startup could not restrict.</summary>
/// <param name="Path">The file.</param>
/// <param name="Mode">Its mode (0 when even that could not be read).</param>
/// <param name="Reason">Why it could not be changed.</param>
public sealed record UnrestrictedFile(string Path, UnixFileMode Mode, string Reason);
