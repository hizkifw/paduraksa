using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Vault;
using Paduraksa.Vault.Crypto;

namespace Paduraksa.Vault.Backup;

/// <summary>A verified archive unpacked into a staging directory (<see cref="IStagedBackup"/>); owns the backup-only key and the directory.</summary>
internal sealed class StagedBackup(
    VaultBackup owner, string stagingDirectory, BackupManifest manifest, string configDatabasePath, string? lastKnownGoodPath, string vaultDatabasePath, Kek backupKek)
    : IStagedBackup
{
    public BackupManifest Manifest => manifest;

    public string ConfigDatabasePath => configDatabasePath;

    public string? LastKnownGoodPath => lastKnownGoodPath;

    public async ValueTask WriteVaultDatabaseAsync(string destinationPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (File.Exists(destinationPath))
        {
            throw new InvalidOperationException($"{destinationPath} exists: the restore moves the current vault database aside before it writes the restored one.");
        }

        // Worked on as a copy, so the verified file in staging stays as it was unpacked.
        var work = Path.Combine(stagingDirectory, "vault.work");
        File.Copy(vaultDatabasePath, work, overwrite: true);

        // An archive from an older build carries an older schema: brought to this build's before the keys are touched.
        var migrator = new Persistence.VaultDbContext(VaultBackup.ScratchOptions(work));
        await using (migrator.ConfigureAwait(false))
        {
            await migrator.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }

        var machine = owner.ResolveMachineKek();
        try
        {
            try
            {
                await owner.RewrapScratchAsync(work, backupKek, machine.Kek, cancellationToken).ConfigureAwait(false);
            }
            catch (KekMismatchException)
            {
                throw new BackupException(BackupFailure.Damaged, "The backup is damaged: its vault database is not under the key the archive holds.");
            }
        }
        finally
        {
            machine.Kek.Dispose();
        }

        SqliteFiles.CompactCopy(work, destinationPath);
        File.Delete(work);
    }

    public ValueTask DisposeAsync()
    {
        backupKek.Dispose();
        try
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: what is left is the unpacked copy, in a directory only the owner can read.
        }

        return ValueTask.CompletedTask;
    }
}
