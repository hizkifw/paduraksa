using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Ids;
using Paduraksa.Storage.Ids;

namespace Paduraksa.Data;

/// <summary>Startup for the config database.</summary>
public static class ConfigDatabase
{
    /// <summary>The database file name inside the data directory (PLAN §5).</summary>
    public const string FileName = "paduraksa.db";

    /// <summary>
    /// Brings the database up to date and makes it safe to create IDs: applies pending migrations (creating the file
    /// if needed), then seeds <paramref name="generator"/> from every Int64Ordered table
    /// (<see cref="Int64OrderedIdSeeding"/>). Run once at startup, before anything reads or writes config.
    /// </summary>
    /// <param name="factory">The registered context factory.</param>
    /// <param name="generator">The Int64Ordered generator new IDs come from; normally <see cref="Int64OrderedIdGenerator.Shared"/>.</param>
    /// <param name="cancellationToken">Cancels the startup.</param>
    /// <returns>What was done, for startup logging.</returns>
    public static async Task<ConfigDatabaseInitialized> InitializeAsync(
        IDbContextFactory<ConfigDbContext> factory, Int64OrderedIdGenerator generator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(generator);

        var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            var observed = await context.ObserveInt64OrderedKeysAsync(generator, cancellationToken).ConfigureAwait(false);
            return new ConfigDatabaseInitialized(pending, observed);
        }
    }
}

/// <summary>The outcome of <see cref="ConfigDatabase.InitializeAsync"/>.</summary>
/// <param name="AppliedMigrations">Migrations applied by this start, oldest first (empty if the schema was current).</param>
/// <param name="ObservedKeys">The largest Int64Ordered key per non-empty table, as passed to the generator.</param>
public sealed record ConfigDatabaseInitialized(
    IReadOnlyList<string> AppliedMigrations, IReadOnlyDictionary<string, long> ObservedKeys);
