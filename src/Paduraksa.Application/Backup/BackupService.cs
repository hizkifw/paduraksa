using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Core.Backup;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Data;

namespace Paduraksa.Application.Backup;

/// <summary>How a backup ended.</summary>
public enum BackupStatus
{
    /// <summary>The archive was written.</summary>
    Created = 1,

    /// <summary>The origin lacks <see cref="Permission.BackupCreate"/>. Audited as denied.</summary>
    Denied = 2,

    /// <summary>The passphrase is not acceptable (<see cref="BackupPassphrase.Check"/>); nothing was read.</summary>
    Invalid = 3,

    /// <summary>Another backup is running; nothing was done.</summary>
    Busy = 4,

    /// <summary>It could not be made (the vault can't start, a database can't be read, no space). Audited as failed, logged.</summary>
    Failed = 5,
}

/// <summary>The outcome of <see cref="BackupService.CreateAsync"/>.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Problem">A sentence that says why not, written for the caller (the API and the dashboard show it).</param>
/// <param name="Detail">For <see cref="BackupStatus.Failed"/>: the underlying message, which can name a path. For the CLI and the log, never for a response.</param>
/// <param name="Bytes">The size of the archive written.</param>
public sealed record BackupResult(BackupStatus Status, string? Problem = null, string? Detail = null, long Bytes = 0)
{
    public bool Succeeded => Status == BackupStatus.Created;
}

/// <summary>An archive waiting to be sent: a private file that is deleted when this is disposed.</summary>
public sealed class BackupDownload(FileStream content, string fileName) : IAsyncDisposable, IDisposable
{
    /// <summary>The archive, positioned at its start.</summary>
    public Stream Content { get; } = content;

    /// <summary>The suggested file name.</summary>
    public string FileName { get; } = fileName;

    /// <summary>Its size in bytes.</summary>
    public long Length => content.Length;

    public void Dispose() => content.Dispose();

    public ValueTask DisposeAsync() => content.DisposeAsync();
}

/// <summary>The outcome of <see cref="BackupService.CreateDownloadAsync"/>: a <see cref="BackupResult"/> and, when it succeeded, the archive.</summary>
public sealed record BackupDownloadResult(BackupResult Result, BackupDownload? Download = null);

/// <summary>Where the pieces of a backup are (the data directory's layout).</summary>
/// <param name="ConfigDatabasePath"><c>paduraksa.db</c>.</param>
/// <param name="LastKnownGoodPath"><c>last-good.json</c>.</param>
/// <param name="RunDirectory"><c>run</c>: where a download's private file is made.</param>
public sealed record BackupLocations(string ConfigDatabasePath, string LastKnownGoodPath, string RunDirectory)
{
    public static BackupLocations In(string dataDirectory) =>
        new(Path.Combine(dataDirectory, ConfigDatabase.FileName), Path.Combine(dataDirectory, LastKnownGoodPathName), Path.Combine(dataDirectory, "run"));

    private const string LastKnownGoodPathName = "last-good.json";
}

/// <summary>
/// The full backup (PLAN §8, "As built (M10)"): checks <see cref="Permission.BackupCreate"/> (the Owner's alone; a token can carry it as a
/// scope), checks the passphrase, has the vault write the encrypted archive and audits the outcome as <c>backup.create</c> with the actor,
/// the time (the event's own) and the archive's size, never the passphrase.
/// </summary>
/// <remarks>
/// Works without the config store being healthy beyond what the permission check needs, and while the server runs (the vault reads both
/// databases with SQLite's backup API). The CLI uses it over a small container of its own, as the identity commands do.
/// </remarks>
public sealed partial class BackupService
{
    /// <summary>The extension of an archive.</summary>
    public const string FileExtension = ".pdkbak";

    private static readonly TimeSpan StaleDownload = TimeSpan.FromHours(1);

    private readonly IVaultBackup _vault;
    private readonly IPermissionEvaluator _permissions;
    private readonly IdentityAudit _audit;
    private readonly IDbContextFactory<ConfigDbContext> _contexts;
    private readonly BackupLocations _locations;
    private readonly TimeProvider _time;
    private readonly ILogger<BackupService> _logger;

    internal BackupService(
        IVaultBackup vault, IPermissionEvaluator permissions, IdentityAudit audit, IDbContextFactory<ConfigDbContext> contexts, BackupLocations locations, TimeProvider time, ILogger<BackupService> logger)
    {
        _vault = vault;
        _permissions = permissions;
        _audit = audit;
        _contexts = contexts;
        _locations = locations;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Writes the archive to <paramref name="destination"/>. Everything is decided and audited here: the caller only provides where the bytes go.
    /// </summary>
    public async Task<BackupResult> CreateAsync(CommandOrigin origin, string? passphrase, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(destination);
        if (!await _permissions.IsAllowedAsync(origin, Permission.BackupCreate, cancellationToken).ConfigureAwait(false))
        {
            await _audit.TryAppendAsync(_contexts, IdentityAudit.Entry(origin, "backup.create", AuditOutcome.Denied)).ConfigureAwait(false);
            return new BackupResult(BackupStatus.Denied, "Only the Owner may make a backup.");
        }

        if (BackupPassphrase.Check(passphrase) is { } problem)
        {
            await _audit.TryAppendAsync(_contexts, IdentityAudit.Entry(origin, "backup.create", AuditOutcome.Invalid)).ConfigureAwait(false);
            return new BackupResult(BackupStatus.Invalid, problem);
        }

        var counted = new CountingStream(destination);
        try
        {
            await _vault.CreateAsync(new BackupSources(_locations.ConfigDatabasePath, _locations.LastKnownGoodPath), passphrase.AsMemory(), counted, cancellationToken).ConfigureAwait(false);
        }
        catch (BackupException ex) when (ex.Reason == BackupFailure.Busy)
        {
            return new BackupResult(BackupStatus.Busy, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or SqliteException or BackupException)
        {
            LogFailed(ex);
            await _audit.TryAppendAsync(_contexts, IdentityAudit.Entry(origin, "backup.create", AuditOutcome.Failed)).ConfigureAwait(false);
            return new BackupResult(BackupStatus.Failed, "The backup could not be made; the server's log says why.", ex.Message);
        }

        var bytes = counted.Written;
        await _audit.TryAppendAsync(_contexts, IdentityAudit.Entry(origin, "backup.create", AuditOutcome.Success, details: w => w.WriteNumber("bytes", bytes))).ConfigureAwait(false);
        return new BackupResult(BackupStatus.Created, Bytes: bytes);
    }

    /// <summary>
    /// <see cref="CreateAsync(CommandOrigin, string?, Stream, CancellationToken)"/> into a private file (owner-only, in the run directory)
    /// that comes back as a download and deletes itself when disposed. The archive is complete before anything is sent, so a failure is an
    /// ordinary error answer and never a half-written download.
    /// </summary>
    public async Task<BackupDownloadResult> CreateDownloadAsync(CommandOrigin origin, string? passphrase, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        // Refused before a file is made, so a denied or invalid request costs nothing; CreateAsync repeats the checks that audit.
        if (!await _permissions.IsAllowedAsync(origin, Permission.BackupCreate, cancellationToken).ConfigureAwait(false) || BackupPassphrase.Check(passphrase) is not null)
        {
            return new BackupDownloadResult(await CreateAsync(origin, passphrase, Stream.Null, cancellationToken).ConfigureAwait(false));
        }

        var file = OpenPrivateFile();
        try
        {
            var result = await CreateAsync(origin, passphrase, file, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                await file.DisposeAsync().ConfigureAwait(false);
                return new BackupDownloadResult(result);
            }

            file.Position = 0;
            var stamp = _time.GetUtcNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            return new BackupDownloadResult(result, new BackupDownload(file, $"paduraksa-backup-{stamp}{FileExtension}"));
        }
        catch
        {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>A new file in the run directory, mode 0600 from its first byte, deleted when closed; older ones a crash left are removed first.</summary>
    private FileStream OpenPrivateFile()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_locations.RunDirectory);
        }
        else
        {
            Directory.CreateDirectory(_locations.RunDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        foreach (var stale in Directory.EnumerateFiles(_locations.RunDirectory, "download-*" + FileExtension))
        {
            if (_time.GetUtcNow() - File.GetLastWriteTimeUtc(stale) > StaleDownload)
            {
                TryDelete(stale);
            }
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.DeleteOnClose | FileOptions.SequentialScan,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(Path.Combine(_locations.RunDirectory, $"download-{Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8))}{FileExtension}"), options);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: it is encrypted, and the next backup tries again.
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A full backup could not be made")]
    private partial void LogFailed(Exception exception);

    /// <summary>Counts what passes through, so the audit event can say how large the archive was.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Written { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            Written += buffer.Length;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
