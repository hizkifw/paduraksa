using Microsoft.EntityFrameworkCore;

namespace Paduraksa.Storage.Sqlite;

/// <summary>Where a database's schema stands against the migrations this build carries, by migration ID, oldest first.</summary>
/// <param name="Applied">What the database's history table lists.</param>
/// <param name="Pending">What this build has that the database lacks: applied at the next start.</param>
/// <param name="Unknown">What the database lists that this build does not have: it was written by a newer build.</param>
public sealed record MigrationState(IReadOnlyList<string> Applied, IReadOnlyList<string> Pending, IReadOnlyList<string> Unknown)
{
    /// <summary>Nothing pending and nothing unknown.</summary>
    public bool IsCurrent => Pending.Count == 0 && Unknown.Count == 0;
}

/// <summary>Reads a database's migration state without changing it.</summary>
public static class MigrationInspector
{
    /// <summary>
    /// Compares the history table with the context's migrations. A database without a history table (a new, empty file) has applied nothing.
    /// EF's own <c>GetPendingMigrationsAsync</c> ignores applied migrations it does not know, so the downgrade case is found here.
    /// </summary>
    public static async Task<MigrationState> InspectAsync(DbContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var known = context.Database.GetMigrations().ToList();
        var knownSet = known.ToHashSet(StringComparer.Ordinal);
        var appliedSet = applied.ToHashSet(StringComparer.Ordinal);
        return new MigrationState(
            applied,
            [.. known.Where(m => !appliedSet.Contains(m))],
            [.. applied.Where(m => !knownSet.Contains(m))]);
    }
}
