using System.Globalization;

using Paduraksa.Data;

namespace Paduraksa.Server.Cli.Doctor;

/// <summary>The data directory exists, this user can write it, the databases are owner-only and the disk has room.</summary>
internal sealed class DataDirectoryCheck : IDoctorCheck
{
    /// <summary>Below this much free space a warning (SQLite's WAL, logs and a backup all grow).</summary>
    internal const long WarnFreeBytes = 1L << 30;

    /// <summary>Below this the databases can't be relied on to commit.</summary>
    internal const long FailFreeBytes = 64L << 20;

    /// <summary>The database files, with the WAL and shared-memory files SQLite keeps beside them while it is open.</summary>
    internal static readonly string[] DatabaseFiles =
    [
        ConfigDatabase.FileName, ConfigDatabase.FileName + "-wal", ConfigDatabase.FileName + "-shm",
        VaultFileName, VaultFileName + "-wal", VaultFileName + "-shm",
    ];

    internal const string VaultFileName = "vault.db";

    private const UnixFileMode GroupAndOther = (UnixFileMode)0b000_111_111;

    public string Name => "data directory";

    public bool NeedsOptions => true;

    public Task<IReadOnlyList<CheckResult>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
    {
        var directory = context.Options!.DataDirectory;
        var files = context.Files;
        List<CheckResult> results = [];

        if (!files.DirectoryExists(directory))
        {
            results.Add(files.FileExists(directory)
                ? CheckResult.Failure("data-directory", $"{directory} is a file, not a directory.", "Point Paduraksa__DataDirectory at a directory (in Docker, the volume mounted at /data).")
                : CheckResult.Warning(
                    "data-directory",
                    $"{directory} does not exist: the first start creates it, empty.",
                    "If this is an upgrade, check that Paduraksa__DataDirectory (in Docker, the volume mounted at /data) is the one the server used before, or the server starts a fresh instance."));
            return Done(results);
        }

        results.Add(files.CanWriteDirectory(directory)
            ? CheckResult.Ok("data-directory", $"{directory} exists and {files.CurrentUser} can write it.")
            : CheckResult.Failure(
                "data-directory",
                $"{directory} is not writable by {files.CurrentUser}: the server cannot start there.",
                $"Give the account the server runs as write access (in the official image that is uid 10001: chown -R 10001:10001 {directory})."));

        results.Add(Permissions(directory, files));
        results.Add(DiskSpace(directory, files));
        return Done(results);
    }

    private static CheckResult Permissions(string directory, IDoctorFileSystem files)
    {
        List<string> loose = [];
        var seen = 0;
        foreach (var name in DatabaseFiles)
        {
            var path = Path.Combine(directory, name);
            if (files.Mode(path) is not { } mode)
            {
                continue;
            }

            seen++;
            if ((mode & GroupAndOther) != 0)
            {
                loose.Add($"{path} (mode {Convert.ToString((int)(mode & (UnixFileMode)0xFFF), 8)})");
            }
        }

        if (loose.Count > 0)
        {
            return CheckResult.Warning(
                "file-permissions",
                "Other users can access " + string.Join(", ", loose) + ": the config database holds password hashes and the audit log, the vault holds sealed secrets.",
                "chmod 600 on each (the server tightens the config database when it starts, but not the vault).");
        }

        return seen == 0
            ? CheckResult.Info("file-permissions", "There are no database files yet.")
            : CheckResult.Ok("file-permissions", $"The {seen.ToString(CultureInfo.InvariantCulture)} database file(s) are readable by their owner only.");
    }

    private static CheckResult DiskSpace(string directory, IDoctorFileSystem files)
    {
        if (files.Space(directory) is not { } space)
        {
            return CheckResult.Info("disk-space", "The free space of the volume could not be read.");
        }

        var text = $"{Gib(space.FreeBytes)} GiB free of {Gib(space.TotalBytes)} GiB.";
        if (space.FreeBytes < FailFreeBytes)
        {
            return CheckResult.Failure("disk-space", $"{text} The databases and logs cannot grow.", "Free space on the volume holding the data directory, or move the logs (Paduraksa__Logs__Directory) and trim their retention.");
        }

        return space.FreeBytes < WarnFreeBytes
            ? CheckResult.Warning("disk-space", $"{text} Low for the databases, the logs and a backup.", "Free space on the volume holding the data directory, or lower the log and metric retention in Settings.")
            : CheckResult.Ok("disk-space", text);
    }

    private static string Gib(long bytes) => (bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture);

    private static Task<IReadOnlyList<CheckResult>> Done(List<CheckResult> results) => Task.FromResult<IReadOnlyList<CheckResult>>(results);
}
