using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Vault;
using Paduraksa.Storage.Sqlite;
using Paduraksa.Vault.Crypto;
using Paduraksa.Vault.Persistence;

namespace Paduraksa.Vault;

/// <summary>
/// <see cref="IVaultInspection"/>: <c>paduraksa doctor</c>'s read-only look at the vault. It goes through the same <see cref="KekProvider"/> the
/// vault starts with (so the source's priority, the key's format and the mode rules are the vault's own), but never generates a key, and it opens
/// <c>vault.db</c> read-only: no migration, no check value stored, nothing created.
/// </summary>
internal sealed class VaultInspector(VaultOptions options, KekProvider keks) : IVaultInspection
{
    public async ValueTask<VaultInspection> InspectAsync(CancellationToken cancellationToken)
    {
        var dataDirectory = options.DataDirectory;
        var databasePath = Path.Combine(dataDirectory, VaultDatabase.FileName);

        KekSourceKind? source = null;
        var beside = false;
        string? problem = null;
        Kek? kek = null;
        try
        {
            var resolved = keks.Resolve(options.GetVariable, dataDirectory, databasePath, generate: false);
            if (resolved is not null)
            {
                kek = resolved.Kek;
                source = resolved.Source;
                beside = resolved.BesideDatabase;
            }
        }
        catch (InvalidOperationException ex)
        {
            // The vault's own sentence (it names the file and the mode, never the key). The source is still known, from the same settings.
            problem = ex.Message;
            (source, beside) = Locate(dataDirectory);
        }

        using (kek)
        {
            var file = KekProvider.KeyFilePath(options.GetVariable, dataDirectory);
            var database = await InspectDatabaseAsync(databasePath, kek, cancellationToken).ConfigureAwait(false);
            return new VaultInspection(
                source, file, beside, problem, database.Exists, database.Migrations.Applied, database.Migrations.Pending, database.Migrations.Unknown, database.Problem, database.KeyCheck);
        }
    }

    /// <summary>The source and placement when the key itself could not be read: decided by the variables alone, in the vault's priority order.</summary>
    private (KekSourceKind? Source, bool Beside) Locate(string dataDirectory)
    {
        if (KekProvider.KeyFilePath(options.GetVariable, dataDirectory) is not { } file)
        {
            return (KekSourceKind.Environment, false);
        }

        var explicitFile = !string.IsNullOrWhiteSpace(options.GetVariable(KekProvider.KeyFileVariable));
        return (explicitFile ? KekSourceKind.File : KekSourceKind.GeneratedFile, !explicitFile || KekProvider.IsBeside(file, dataDirectory));
    }

    private sealed record DatabaseLook(bool Exists, MigrationState Migrations, string? Problem, VaultKeyCheck KeyCheck);

    private static async Task<DatabaseLook> InspectDatabaseAsync(string databasePath, Kek? kek, CancellationToken cancellationToken)
    {
        MigrationState none = new([], [], []);
        if (!File.Exists(databasePath))
        {
            return new DatabaseLook(false, none, null, VaultKeyCheck.NotChecked);
        }

        try
        {
            var builder = new DbContextOptionsBuilder<VaultDbContext>();
            builder.UsePaduraksaSqlite(PaduraksaSqlite.ReadOnlyConnectionString(databasePath));
            var context = new VaultDbContext(builder.Options);
            await using (context.ConfigureAwait(false))
            {
                var migrations = await MigrationInspector.InspectAsync(context, cancellationToken).ConfigureAwait(false);

                // A database a newer build wrote may not have the shape this build reads, and one with no history is empty: neither is compared.
                if (kek is null || migrations.Unknown.Count > 0 || migrations.Applied.Count == 0)
                {
                    return new DatabaseLook(true, migrations, null, VaultKeyCheck.NotChecked);
                }

                var meta = await context.Meta.AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                var check = meta is null ? VaultKeyCheck.NotChecked : kek.Matches(meta.KekCheck) ? VaultKeyCheck.Matches : VaultKeyCheck.DoesNotMatch;
                return new DatabaseLook(true, migrations, null, check);
            }
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new DatabaseLook(true, none, ex.Message, VaultKeyCheck.NotChecked);
        }
    }
}
