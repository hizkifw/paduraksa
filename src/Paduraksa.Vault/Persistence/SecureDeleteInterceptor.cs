using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Paduraksa.Storage.Sqlite;

namespace Paduraksa.Vault.Persistence;

/// <summary>
/// Turns on <c>PRAGMA secure_delete</c> for every connection to <c>vault.db</c>: SQLite then overwrites deleted and replaced
/// content with zeros instead of leaving it in the free space of its page. The vault database is small and written rarely, so
/// the cost is nothing, and it is what keeps a replaced secret's old wrapped key, a deleted secret and the
/// previous value of a MAC key from lingering in the file. (The write-ahead log is dealt with by a checkpoint after a KEK
/// rotation.) It is the vault's own setting on top of the shared ones in <see cref="PaduraksaSqlite"/>.
/// </summary>
internal sealed class SecureDeleteInterceptor : DbConnectionInterceptor
{
    public static SecureDeleteInterceptor Instance { get; } = new();

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA secure_delete = ON;";
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = "PRAGMA secure_delete = ON;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Registration of the vault database's context factory.</summary>
internal static class VaultDbContextRegistration
{
    /// <summary>A singleton <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{TContext}"/> for <c>vault.db</c> at <paramref name="databasePath"/>: the shared SQLite setup plus secure delete.</summary>
    public static IServiceCollection AddVaultDbContextFactory(this IServiceCollection services, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        var connectionString = PaduraksaSqlite.ConnectionString(databasePath);
        return services.AddDbContextFactory<VaultDbContext>(
            options => options
                .UsePaduraksaSqlite(connectionString)
                .AddInterceptors(SecureDeleteInterceptor.Instance)
                // A migration that widens a check constraint rebuilds the table, and EF warns that its "PRAGMA foreign_keys = 0" is not transactional. The
                // dependents of Secrets (TotpStates cascades on delete) surviving such a rebuild is pinned by VaultDatabaseTests, so the warning is noise on every new install.
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.NonTransactionalMigrationOperationWarning)));
    }
}
