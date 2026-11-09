using Microsoft.EntityFrameworkCore;
using Paduraksa.Vault.Crypto;

namespace Paduraksa.Vault.Persistence;

/// <summary>Startup for the vault database.</summary>
internal static class VaultDatabase
{
    /// <summary>The database file name inside the data directory.</summary>
    public const string FileName = "vault.db";

    /// <summary>The <see cref="Exception.Data"/> key under which a KEK mismatch carries the database's check value.</summary>
    public const string StoredCheckKey = "Paduraksa.Vault.StoredKekCheck";

    /// <summary>
    /// Brings the database up to date and proves the KEK is the one it was written under: creates the file if needed
    /// (mode 0600 on Unix, and SQLite gives the <c>-wal</c> and <c>-shm</c> files the same mode), applies pending
    /// migrations, then compares <paramref name="kek"/> with the stored check value. A database with no check value yet
    /// and no secrets (a new one) stores the KEK's. One that has secrets but no check value is refused.
    /// </summary>
    /// <exception cref="InvalidOperationException">The KEK is not the database's, or the database is damaged.</exception>
    public static async Task<VaultDatabaseInitialized> InitializeAsync(
        IDbContextFactory<VaultDbContext> factory, string databasePath, Kek kek, TimeProvider time, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(kek);
        ArgumentNullException.ThrowIfNull(time);

        EnsureFile(databasePath);

        IReadOnlyList<string> pending;
        var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var meta = await context.Meta.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (meta is null)
                {
                    var holdsData = await context.Secrets.AnyAsync(cancellationToken).ConfigureAwait(false)
                        || await context.AccountKeys.AnyAsync(cancellationToken).ConfigureAwait(false)
                        || await context.CertificateKeys.AnyAsync(cancellationToken).ConfigureAwait(false)
                        || await context.StagedKeys.AnyAsync(cancellationToken).ConfigureAwait(false)
                        || await context.DataProtectionKeys.AnyAsync(cancellationToken).ConfigureAwait(false);
                    if (holdsData)
                    {
                        throw new InvalidOperationException(
                            $"{databasePath} holds sealed values but no master-key check value, so the master key cannot be verified: the database is damaged.");
                    }

                    var now = time.GetUtcNow();
                    context.Meta.Add(new VaultMetaRow { KekCheck = kek.CheckValue, CreatedAt = now, UpdatedAt = now });
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                else if (!kek.Matches(meta.KekCheck))
                {
                    var mismatch = new InvalidOperationException(
                        $"The master key does not match {databasePath}: it was written under a different key. "
                        + $"Use the original key ({KekProvider.KeyFileVariable}, {KekProvider.KeyVariable} or master.key), or restore it from a backup.");

                    // So a start can look for the key that does match (VaultRuntime), without a new exception type.
                    mismatch.Data[StoredCheckKey] = meta.KekCheck;
                    throw mismatch;
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return new VaultDatabaseInitialized(pending);
    }

    private static void EnsureFile(string databasePath)
    {
        if (File.Exists(databasePath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.ReadWrite };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            // An empty file is a valid new SQLite database, and this way it is never world-readable, not even briefly.
            new FileStream(databasePath, options).Dispose();
        }
        catch (IOException) when (File.Exists(databasePath))
        {
            // Created by another process in the meantime.
        }
    }
}

/// <summary>The outcome of <see cref="VaultDatabase.InitializeAsync"/>.</summary>
/// <param name="AppliedMigrations">Migrations applied by this start, oldest first (empty if the schema was current).</param>
internal sealed record VaultDatabaseInitialized(IReadOnlyList<string> AppliedMigrations);
