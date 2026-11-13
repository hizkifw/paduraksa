using System.Formats.Tar;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Paduraksa.Core.Backup;
using Paduraksa.Core.Vault;
using Paduraksa.Vault.Crypto;
using Paduraksa.Vault.Persistence;

namespace Paduraksa.Vault.Backup;

/// <summary>
/// Full backup and restore (<see cref="IVaultBackup"/>); the format and the reasons for it are in docs/design/vault-and-certificates.md "Backup and restore (M10)".
/// </summary>
/// <remarks>
/// <para>
/// <b>What the archive is.</b> A tar (forward-only, so it is written and read as a stream, unlike a zip, whose reader needs to seek)
/// inside the chunked AES-256-GCM of <see cref="AeadChunk"/>, under a key Argon2id derives from the passphrase. Everything but the
/// 48-byte header is encrypted: the config database holds password hashes, TOTP references, audit entries and client addresses, so
/// it is as sensitive as the vault. The entries are a manifest first, then <c>paduraksa.db</c>, <c>vault.db</c>,
/// <c>last-good.json</c> and <c>vault-backup.key</c>.
/// </para>
/// <para>
/// <b>The master key never enters it.</b> The archive's <c>vault.db</c> is a snapshot whose every data-encryption key was re-wrapped
/// (<see cref="KekRotation.RewrapAllAsync"/>) under a fresh, backup-only key-encryption key, which is stored in the encrypted archive. A
/// restore re-wraps them again under the target's own master key, so a restored machine never adopts the source's, and a source whose
/// master key is later lost or rotated away can still be restored.
/// </para>
/// <para>
/// <b>Consistency.</b> Each database is read with SQLite's online backup API (one step, one read transaction), so each snapshot is the
/// state at one instant while the server keeps writing. The two instants are not the same: the config database is read first and the
/// vault second, so every key the config's certificate rows point at is already in the vault snapshot (issuance writes the key
/// first); a certificate deleted in the few milliseconds between them is the one thing that can come out inconsistent, and it comes
/// out as a certificate row without a key, which the startup recovery already handles.
/// </para>
/// </remarks>
internal sealed partial class VaultBackup(VaultRuntime runtime, KekProvider keks, VaultOptions options, ILogger<VaultBackup> logger) : IVaultBackup, IDisposable
{
    private static readonly string Version =
        typeof(VaultBackup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    private readonly SemaphoreSlim _busy = new(1, 1);

    public void Dispose() => _busy.Dispose();

    public async ValueTask<BackupManifest> CreateAsync(BackupSources sources, ReadOnlyMemory<char> passphrase, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(destination);
        if (BackupPassphrase.Check(passphrase.Span) is { } problem)
        {
            throw new BackupException(BackupFailure.WeakPassphrase, problem);
        }

        return await CreateAsync(sources, passphrase, destination, BackupHeader.DefaultMemoryKiB, BackupHeader.DefaultIterations, BackupHeader.DefaultParallelism, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The same with explicit key-derivation costs: the tests' lighter setting; the public entry point always uses the defaults.</summary>
    internal async ValueTask<BackupManifest> CreateAsync(
        BackupSources sources, ReadOnlyMemory<char> passphrase, Stream destination, int memoryKiB, int iterations, int parallelism, CancellationToken cancellationToken)
    {
        if (!_busy.Wait(0, CancellationToken.None))
        {
            throw new BackupException(BackupFailure.Busy, "A backup is already being made. Try again when it is done.");
        }

        try
        {
            await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            var runDirectory = Path.Combine(options.DataDirectory, "run");
            var workDirectory = Path.Combine(runDirectory, "backup-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)));
            SqliteFiles.CreatePrivateDirectory(workDirectory);
            // Held for the backup's whole life: another process's sweep (the CLI's, while this runs in the server) skips a locked directory.
            await using var marker = HoldMarker(workDirectory);
            SweepStale(runDirectory);
            try
            {
                var manifest = await WriteAsync(sources, passphrase, destination, workDirectory, memoryKiB, iterations, parallelism, cancellationToken).ConfigureAwait(false);
                var total = TotalBytes(manifest);
                var count = manifest.Entries.Count;
                LogCreated(total, count);
                return manifest;
            }
            finally
            {
                TryDelete(workDirectory);
            }
        }
        finally
        {
            _busy.Release();
        }
    }

    public async ValueTask<IStagedBackup> OpenAsync(
        Stream archive, ReadOnlyMemory<char> passphrase, string stagingDirectory, IReadOnlyCollection<string> knownConfigMigrations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentNullException.ThrowIfNull(knownConfigMigrations);
        if (passphrase.IsEmpty)
        {
            throw new BackupException(BackupFailure.WrongPassphrase, "A passphrase is required.");
        }

        var header = new byte[BackupHeader.Length];
        if (archive.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
        {
            throw new BackupException(BackupFailure.NotABackup, "This file is not a Paduraksa backup.");
        }

        var parsed = BackupHeader.Parse(header);
        cancellationToken.ThrowIfCancellationRequested();
        var key = await Task.Run(() => BackupKeyDerivation.Derive(passphrase.Span, parsed.Salt, parsed.MemoryKiB, parsed.Iterations, parsed.Parallelism), cancellationToken).ConfigureAwait(false);
        try
        {
            SqliteFiles.CreatePrivateDirectory(stagingDirectory);
            StagedBackup? staged = null;
            try
            {
                staged = await Task.Run(() => Unpack(archive, key, header, parsed, stagingDirectory, knownConfigMigrations), cancellationToken).ConfigureAwait(false);
                return staged;
            }
            catch
            {
                if (staged is null)
                {
                    TryDelete(stagingDirectory);
                }

                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private async Task<BackupManifest> WriteAsync(
        BackupSources sources, ReadOnlyMemory<char> passphrase, Stream destination, string workDirectory, int memoryKiB, int iterations, int parallelism, CancellationToken cancellationToken)
    {
        var configCopy = Path.Combine(workDirectory, BackupEntries.Config);
        var vaultCopy = Path.Combine(workDirectory, BackupEntries.Vault);
        var rawVault = Path.Combine(workDirectory, "vault.raw");

        // The config database first, the vault second (see the type's remarks); the vault under its gate, so no rotation changes the
        // master key between the snapshot and the re-wrap.
        SqliteFiles.Snapshot(sources.ConfigDatabasePath, configCopy);
        using var backupKek = Kek.Generate();
        using (var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            SqliteFiles.Snapshot(runtime.DatabasePath, rawVault);
            try
            {
                await RewrapScratchAsync(rawVault, runtime.Kek, backupKek, cancellationToken).ConfigureAwait(false);
            }
            catch (KekMismatchException)
            {
                throw new InvalidOperationException(
                    "The vault database is under another master key than the one this process holds (a rotation ran elsewhere): restart the service with the current key, then back up again.");
            }
        }

        SqliteFiles.CompactCopy(rawVault, vaultCopy);
        File.Delete(rawVault);

        List<(string Name, string Path)> files = [(BackupEntries.Config, configCopy), (BackupEntries.Vault, vaultCopy)];
        if (sources.LastKnownGoodPath is { } lastGood && File.Exists(lastGood))
        {
            var lastGoodCopy = Path.Combine(workDirectory, BackupEntries.LastKnownGood);
            File.Copy(lastGood, lastGoodCopy);
            files.Add((BackupEntries.LastKnownGood, lastGoodCopy));
        }

        var keyText = GC.AllocateArray<byte>(Kek.FileLength, pinned: true);
        try
        {
            backupKek.WriteEncoded(keyText);
            keyText[Kek.TextLength] = (byte)'\n';

            List<BackupEntry> entries = [];
            foreach (var (name, path) in files)
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                entries.Add(new BackupEntry(name, stream.Length, Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))));
            }

            entries.Add(new BackupEntry(BackupEntries.VaultKey, keyText.Length, Convert.ToHexStringLower(SHA256.HashData(keyText))));

            var header = BackupHeader.Create(memoryKiB, iterations, parallelism, RandomNumberGenerator.GetBytes(BackupHeader.SaltLength));
            var manifest = new BackupManifest(
                BackupHeader.CurrentFormat,
                Version,
                runtime.Time.GetUtcNow(),
                SqliteFiles.Migrations(configCopy),
                SqliteFiles.Migrations(vaultCopy),
                entries,
                header.Kdf);

            var key = await Task.Run(() => BackupKeyDerivation.Derive(passphrase.Span, header.Salt, memoryKiB, iterations, parallelism), cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Run(() => WriteArchive(destination, key, header, manifest, files, keyText), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            return manifest;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyText);
        }
    }

    private static void WriteArchive(Stream destination, byte[] key, BackupHeader header, BackupManifest manifest, List<(string Name, string Path)> files, byte[] keyText)
    {
        using var encrypted = new AeadEncryptStream(destination, key, header);
        using (var tar = new TarWriter(encrypted, TarEntryFormat.Ustar, leaveOpen: true))
        {
            var created = manifest.CreatedAt;
            tar.WriteEntry(Entry(BackupEntries.Manifest, created, new MemoryStream(BackupManifestFormat.Write(manifest))));
            foreach (var (name, path) in files)
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                tar.WriteEntry(Entry(name, created, file));
            }

            using MemoryStream keyStream = new(keyText, writable: false);
            tar.WriteEntry(Entry(BackupEntries.VaultKey, created, keyStream));
        }

        encrypted.Complete();
    }

    private static UstarTarEntry Entry(string name, DateTimeOffset modified, Stream data) =>
        new(TarEntryType.RegularFile, name)
        {
            DataStream = data,
            ModificationTime = modified,
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };

    private StagedBackup Unpack(Stream archive, byte[] key, byte[] header, BackupHeader parsed, string stagingDirectory, IReadOnlyCollection<string> knownConfigMigrations)
    {
        using var decrypted = new AeadDecryptStream(archive, key, header);
        BackupManifest? manifest = null;
        Dictionary<string, string> staged = [];
        Kek? backupKek = null;
        try
        {
            using (var tar = new TarReader(decrypted, leaveOpen: true))
            {
                while (ReadEntry(tar) is { } entry)
                {
                    if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream is null || !BackupEntries.Allowed.Contains(entry.Name) || (manifest is null) != (entry.Name == BackupEntries.Manifest) || staged.ContainsKey(entry.Name))
                    {
                        throw new BackupException(BackupFailure.Damaged, "The backup is damaged: it holds an entry it should not, or in the wrong order.");
                    }

                    if (entry.Name == BackupEntries.Manifest)
                    {
                        manifest = BackupManifestFormat.Parse(ReadAll(entry.DataStream, 1024 * 1024));
                        if (manifest.Kdf != parsed.Kdf || manifest.Entries.Select(e => e.Name).Distinct().Count() != manifest.Entries.Count)
                        {
                            throw new BackupException(BackupFailure.Damaged, "The backup is damaged: its header and manifest disagree.");
                        }

                        staged[BackupEntries.Manifest] = string.Empty;
                        continue;
                    }

                    var expected = manifest!.Entries.FirstOrDefault(e => e.Name == entry.Name)
                        ?? throw new BackupException(BackupFailure.Damaged, $"The backup is damaged: {entry.Name} is not in its manifest.");
                    if (entry.Length != expected.Bytes)
                    {
                        throw new BackupException(BackupFailure.Damaged, $"The backup is damaged: {entry.Name} does not match its manifest.");
                    }

                    var path = Path.Combine(stagingDirectory, entry.Name);
                    staged[entry.Name] = path;
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long written = 0;
                    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                    if (!OperatingSystem.IsWindows())
                    {
                        options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    }

                    using (var output = new FileStream(path, options))
                    {
                        var buffer = new byte[81920];
                        int read;
                        while ((read = entry.DataStream.Read(buffer)) > 0)
                        {
                            hash.AppendData(buffer.AsSpan(0, read));
                            output.Write(buffer.AsSpan(0, read));
                            written += read;
                        }
                    }

                    if (written != expected.Bytes || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), expected.Sha256, StringComparison.Ordinal))
                    {
                        throw new BackupException(BackupFailure.Damaged, $"The backup is damaged: {entry.Name} does not match its manifest.");
                    }
                }
            }

            DrainToEnd(decrypted);
            if (manifest is null || !BackupEntries.Required.All(staged.ContainsKey) || manifest.Entries.Any(e => !staged.ContainsKey(e.Name)))
            {
                throw new BackupException(BackupFailure.Damaged, "The backup is damaged: it is missing a file its manifest lists.");
            }

            backupKek = ReadBackupKek(staged[BackupEntries.VaultKey]);
            File.Delete(staged[BackupEntries.VaultKey]);
            CheckDatabase(staged[BackupEntries.Config], BackupEntries.Config, knownConfigMigrations);
            CheckDatabase(staged[BackupEntries.Vault], BackupEntries.Vault, VaultMigrations());
            return new StagedBackup(this, stagingDirectory, manifest, staged[BackupEntries.Config], staged.GetValueOrDefault(BackupEntries.LastKnownGood), staged[BackupEntries.Vault], backupKek);
        }
        catch
        {
            backupKek?.Dispose();
            throw;
        }
    }

    /// <summary>A tar that is not one (a block that is not a header) is a damaged archive, not an I/O error.</summary>
    private static TarEntry? ReadEntry(TarReader tar)
    {
        try
        {
            return tar.GetNextEntry(copyData: false);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException)
        {
            throw new BackupException(BackupFailure.Damaged, "The backup is damaged: its contents are not readable.");
        }
    }

    /// <summary>Reads to the end of the encrypted stream so the last chunk is authenticated; what follows the tar's end is padding and must be zeros.</summary>
    private static void DrainToEnd(AeadDecryptStream decrypted)
    {
        var buffer = new byte[BackupHeader.ChunkSize];
        int read;
        while ((read = decrypted.Read(buffer)) > 0)
        {
            if (buffer.AsSpan(0, read).ContainsAnyExcept((byte)0))
            {
                throw new BackupException(BackupFailure.Damaged, "The backup is damaged: there is data after its last file.");
            }
        }
    }

    private static byte[] ReadAll(Stream stream, int limit)
    {
        using MemoryStream copy = new();
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            copy.Write(buffer.AsSpan(0, read));
            if (copy.Length > limit)
            {
                throw new BackupException(BackupFailure.Damaged, "The backup is damaged: its manifest is too large.");
            }
        }

        return copy.ToArray();
    }

    private static Kek ReadBackupKek(string path)
    {
        var text = GC.AllocateArray<byte>(Kek.FileLength + 1, pinned: true);
        var chars = GC.AllocateArray<char>(Kek.FileLength + 1, pinned: true);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0);
            var length = stream.ReadAtLeast(text, text.Length, throwOnEndOfStream: false);
            if (length > Kek.FileLength)
            {
                throw new BackupException(BackupFailure.Damaged, "The backup is damaged: its vault key is not a key.");
            }

            var count = System.Text.Encoding.ASCII.GetChars(text.AsSpan(0, length), chars);
            return Kek.Parse(chars.AsSpan(0, count));
        }
        catch (FormatException)
        {
            throw new BackupException(BackupFailure.Damaged, "The backup is damaged: its vault key is not a key.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(text);
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(chars.AsSpan()));
        }
    }

    /// <summary>The file is a sound SQLite database whose schema this build knows (an older one is migrated later; a newer one can't be read).</summary>
    private static void CheckDatabase(string path, string what, IReadOnlyCollection<string> known)
    {
        if (!SqliteFiles.IsSound(path))
        {
            throw new BackupException(BackupFailure.Damaged, $"The backup is damaged: {what} is not a sound database.");
        }

        var unknown = SqliteFiles.Migrations(path).Where(id => !known.Contains(id)).ToList();
        if (unknown.Count > 0)
        {
            throw new BackupException(
                BackupFailure.SchemaTooNew,
                $"This backup's {what} is from a newer Paduraksa than this one (it has migration {unknown[^1]}, which this build does not know). Upgrade Paduraksa, then restore it. Nothing was changed.");
        }
    }

    /// <summary>The migration IDs of this build's vault schema.</summary>
    internal static IReadOnlyList<string> VaultMigrations()
    {
        using VaultDbContext context = new(new DbContextOptionsBuilder<VaultDbContext>().UseSqlite("Data Source=:memory:").Options);
        return [.. context.Database.GetMigrations()];
    }

    /// <summary>Re-wraps a scratch copy of the vault database under <paramref name="newKek"/> and records it as that database's key.</summary>
    internal async Task RewrapScratchAsync(string path, Kek oldKek, Kek newKek, CancellationToken cancellationToken)
    {
        var context = new VaultDbContext(ScratchOptions(path));
        await using (context.ConfigureAwait(false))
        {
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var meta = await context.Meta.SingleAsync(cancellationToken).ConfigureAwait(false);
                if (!oldKek.Matches(meta.KekCheck))
                {
                    throw new KekMismatchException();
                }

                await KekRotation.RewrapAllAsync(context, oldKek, newKek, cancellationToken).ConfigureAwait(false);
                meta.KekCheck = newKek.CheckValue;
                meta.UpdatedAt = runtime.Time.GetUtcNow();
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>A context over a scratch file: no write-ahead log (it is a file that is copied and deleted), no pooling (nothing keeps it open).</summary>
    internal static DbContextOptions<VaultDbContext> ScratchOptions(string path) =>
        new DbContextOptionsBuilder<VaultDbContext>()
            .UseSqlite(SqliteFiles.Connection(path, Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite))
            .AddInterceptors(SecureDeleteInterceptor.Instance)
            .ConfigureWarnings(warnings => warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.NonTransactionalMigrationOperationWarning))
            .Options;

    internal ResolvedKek ResolveMachineKek() => keks.Resolve(options.GetVariable, options.DataDirectory, runtime.DatabasePath);

    internal TimeProvider Time => runtime.Time;

    private const string MarkerName = ".lock";

    /// <summary>An exclusive lock on a marker file in <paramref name="workDirectory"/>, which the operating system releases when the process ends however it ends.</summary>
    private static FileStream HoldMarker(string workDirectory) =>
        new(Path.Combine(workDirectory, MarkerName), new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 0 });

    /// <summary>
    /// Removes the work directories a crash left (they hold plain snapshots). A directory is abandoned when nobody holds its marker, so a
    /// backup that runs for hours, in this process or in the CLI's, is never touched; one with no marker at all (a crash between making the
    /// directory and locking it) is abandoned once it is an hour old.
    /// </summary>
    private void SweepStale(string runDirectory)
    {
        foreach (var directory in Directory.EnumerateDirectories(runDirectory, "backup-*"))
        {
            var marker = Path.Combine(directory, MarkerName);
            if (File.Exists(marker))
            {
                try
                {
                    new FileStream(marker, FileMode.Open, FileAccess.Write, FileShare.None, bufferSize: 0).Dispose();
                }
                catch (IOException)
                {
                    continue; // Held: a backup is running there.
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                TryDelete(directory);
            }
            else if (runtime.Time.GetUtcNow() - Directory.GetLastWriteTimeUtc(directory) > TimeSpan.FromHours(1))
            {
                TryDelete(directory);
            }
        }
    }

    private static long TotalBytes(BackupManifest manifest) => manifest.Entries.Sum(e => e.Bytes);

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the next backup or restore creates its own directory, and the contents are encrypted or re-creatable.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "A backup was written: {Bytes} bytes of data in {Entries} files (the file is encrypted under the passphrase)")]
    private partial void LogCreated(long bytes, int entries);
}
