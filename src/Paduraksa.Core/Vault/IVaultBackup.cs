namespace Paduraksa.Core.Vault;

/// <summary>What a backup is taken from: the config database and last-known-good file next to the vault (which the vault knows).</summary>
/// <param name="ConfigDatabasePath">The live config database. It is read through SQLite's backup API, never copied as a file.</param>
/// <param name="LastKnownGoodPath">The last-known-good file, or null; a missing file is left out.</param>
public sealed record BackupSources(string ConfigDatabasePath, string? LastKnownGoodPath);

/// <summary>The key derivation an archive was sealed with.</summary>
/// <param name="Algorithm">Always <c>argon2id</c> in format 1.</param>
/// <param name="Version">The Argon2 version number (19, 0x13).</param>
/// <param name="MemoryKiB">Memory cost in KiB.</param>
/// <param name="Iterations">Passes over the memory.</param>
/// <param name="Parallelism">Lanes.</param>
public sealed record BackupKdf(string Algorithm, int Version, int MemoryKiB, int Iterations, int Parallelism);

/// <summary>One file of an archive.</summary>
/// <param name="Name">The entry's fixed name (<c>paduraksa.db</c>, <c>vault.db</c>, <c>last-good.json</c>, <c>vault-backup.key</c>).</param>
/// <param name="Bytes">Its size.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of its content.</param>
public sealed record BackupEntry(string Name, long Bytes, string Sha256);

/// <summary>What an archive says about itself (its sealed <c>manifest.json</c>); readable only with the passphrase.</summary>
/// <param name="Format">The archive format version (1).</param>
/// <param name="PaduraksaVersion">The build that made it.</param>
/// <param name="CreatedAt">When the snapshots were taken.</param>
/// <param name="ConfigMigrations">The EF migration IDs applied to the config database, oldest first.</param>
/// <param name="VaultMigrations">The same for the vault database.</param>
/// <param name="Entries">Every file with its size and hash.</param>
/// <param name="Kdf">The passphrase key derivation (also in the plain header, since it is needed before anything can be read).</param>
public sealed record BackupManifest(
    int Format,
    string PaduraksaVersion,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> ConfigMigrations,
    IReadOnlyList<string> VaultMigrations,
    IReadOnlyList<BackupEntry> Entries,
    BackupKdf Kdf);

/// <summary>Why a backup or a restore was refused (<see cref="BackupException.Reason"/>).</summary>
public enum BackupFailure
{
    /// <summary>The file is not a Paduraksa backup (wrong or missing magic, or too short to hold the header).</summary>
    NotABackup = 0,

    /// <summary>The archive's format is newer than this build reads, or its key derivation is outside what it accepts.</summary>
    UnsupportedFormat = 1,

    /// <summary>The passphrase is wrong (or the header was altered: the two cannot be told apart).</summary>
    WrongPassphrase = 2,

    /// <summary>The archive was changed or cut short, or its contents disagree with its manifest. Nothing was written.</summary>
    Damaged = 3,

    /// <summary>The databases in the archive are from a newer Paduraksa than this one (a newer schema this build can't read).</summary>
    SchemaTooNew = 4,

    /// <summary>Another backup is being made by this process.</summary>
    Busy = 5,

    /// <summary>The passphrase is not acceptable (<c>BackupPassphrase.Check</c>).</summary>
    WeakPassphrase = 6,
}

/// <summary>A backup or restore could not be done. The message names the problem and never the passphrase or any key.</summary>
public sealed class BackupException(BackupFailure reason, string message) : Exception(message)
{
    /// <summary>Why it was refused.</summary>
    public BackupFailure Reason { get; } = reason;
}

/// <summary>
/// Full backup and restore: one passphrase-encrypted archive of the config database, the vault database and the last-known-good
/// file (PLAN §8, As built (M10)). The vault owns the encryption, so the passphrase and every key stay inside it; the archive
/// holds the vault's keys re-wrapped under a backup-only key and never this machine's master key.
/// </summary>
public interface IVaultBackup
{
    /// <summary>
    /// Writes an archive of <paramref name="sources"/> and the vault to <paramref name="destination"/> while the server runs: both
    /// databases are read with SQLite's backup API. Nothing is returned that could open it without the passphrase.
    /// </summary>
    /// <exception cref="BackupException"><see cref="BackupFailure.WeakPassphrase"/> or <see cref="BackupFailure.Busy"/>.</exception>
    /// <exception cref="InvalidOperationException">The vault can't start (no usable master key), or the database is under another key.</exception>
    ValueTask<BackupManifest> CreateAsync(BackupSources sources, ReadOnlyMemory<char> passphrase, Stream destination, CancellationToken cancellationToken);

    /// <summary>
    /// Decrypts and checks the whole archive into <paramref name="stagingDirectory"/> (created, owner-only; removed by disposing the
    /// result) before anything of the installation is touched: the header, the passphrase, every chunk's authentication tag, the
    /// manifest, every hash and the schemas, which must not be newer than this build's (<paramref name="knownConfigMigrations"/> are
    /// the config database's).
    /// </summary>
    /// <exception cref="BackupException">The archive can't be used; see <see cref="BackupFailure"/>.</exception>
    ValueTask<IStagedBackup> OpenAsync(
        Stream archive, ReadOnlyMemory<char> passphrase, string stagingDirectory, IReadOnlyCollection<string> knownConfigMigrations, CancellationToken cancellationToken);
}

/// <summary>A verified archive, unpacked into a private staging directory. Disposing it removes the staging directory.</summary>
public interface IStagedBackup : IAsyncDisposable
{
    /// <summary>The verified manifest.</summary>
    BackupManifest Manifest { get; }

    /// <summary>The config database, plain, as the source's snapshot had it (migrated on the next start, as always).</summary>
    string ConfigDatabasePath { get; }

    /// <summary>The last-known-good file, or null if the archive has none.</summary>
    string? LastKnownGoodPath { get; }

    /// <summary>
    /// Writes the vault database to <paramref name="destinationPath"/> (which must not exist), migrated to this build's schema and with
    /// every key re-wrapped under <i>this machine's</i> master key, resolved as the server resolves it. When none is configured and
    /// <c>master.key</c> does not exist, one is generated, as at a first start; an existing <c>vault.db</c> beside it that holds data
    /// makes that refuse, so move it aside first.
    /// </summary>
    ValueTask WriteVaultDatabaseAsync(string destinationPath, CancellationToken cancellationToken);
}
