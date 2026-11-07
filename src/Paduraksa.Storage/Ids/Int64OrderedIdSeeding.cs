using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Paduraksa.Core.Ids;

namespace Paduraksa.Storage.Ids;

/// <summary>
/// Startup hook for <see cref="IdKind.Int64Ordered"/> IDs: after a restart the clock may be behind the last persisted
/// ID, so the generator must observe <c>MAX(id)</c> of every table it feeds before it issues new IDs (PLAN §4.2).
/// </summary>
public static class Int64OrderedIdSeeding
{
    /// <summary>
    /// Finds every table whose primary key is a single <see cref="IdKind.Int64Ordered"/> typed ID, reads its
    /// <c>MAX(id)</c> and passes it to <see cref="Int64OrderedIdGenerator.Observe"/>. Run once at startup, before
    /// anything creates IDs; new Int64Ordered tables are picked up automatically.
    /// </summary>
    /// <remarks>
    /// Generated <c>New()</c> uses <see cref="Int64OrderedIdGenerator.Shared"/> for every Int64Ordered ID type, so all
    /// tables are observed into the same generator. <c>MAX</c> of an <c>INTEGER PRIMARY KEY</c> is a single rowid
    /// B-tree lookup in SQLite, so this stays cheap however large the tables get.
    /// </remarks>
    /// <returns>The largest key found per table (empty tables are left out), for startup logging.</returns>
    public static async Task<IReadOnlyDictionary<string, long>> ObserveInt64OrderedKeysAsync(
        this DbContext context, Int64OrderedIdGenerator generator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(generator);

        var sql = context.GetService<ISqlGenerationHelper>();
        var observed = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (table, query) in MaxKeyQueries(context.Model, sql))
        {
            var max = await context.Database.SqlQueryRaw<long?>(query).SingleAsync(cancellationToken).ConfigureAwait(false);
            if (max is { } value)
            {
                generator.Observe(value);
                observed[table] = value;
            }
        }

        return observed;
    }

    private static IEnumerable<(string Table, string Query)> MaxKeyQueries(IModel model, ISqlGenerationHelper sql)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entityType in model.GetEntityTypes())
        {
            if (entityType.FindPrimaryKey()?.Properties is not [var key]
                || !TypedIdMapping.TryGet(key.ClrType, out var mapping)
                || mapping.ValueType != typeof(long)
                || entityType.GetTableName() is not { } table)
            {
                continue;
            }

            var store = StoreObjectIdentifier.Table(table, entityType.GetSchema());
            if (key.GetColumnName(store) is not { } column || !seen.Add(store.DisplayName()))
            {
                continue;
            }

            // SqlQueryRaw<scalar> expects the column to be called "Value".
            yield return (
                store.DisplayName(),
                $"SELECT MAX({sql.DelimitIdentifier(column)}) AS {sql.DelimitIdentifier("Value")} "
                + $"FROM {sql.DelimitIdentifier(table, entityType.GetSchema())}");
        }
    }
}
