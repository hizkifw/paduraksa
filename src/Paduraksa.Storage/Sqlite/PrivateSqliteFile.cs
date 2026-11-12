using System.Runtime.Versioning;

namespace Paduraksa.Storage.Sqlite;

/// <summary>
/// A database file only its owner can read: the vault's sealed rows and the config database's password hashes, session and token hashes and
/// audit log are not for the data directory's other readers, whatever the process umask or the directory's mode is. SQLite gives the
/// <c>-wal</c> and <c>-shm</c> files the mode of the main file when it creates them, so creating the main file 0600 covers them.
/// </summary>
public static class PrivateSqliteFile
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode ModeBits = (UnixFileMode)0xFFF;
    private const UnixFileMode Others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private static readonly string[] Suffixes = ["", "-wal", "-shm"];

    /// <summary>
    /// Creates the file empty with mode 0600 if it is missing (an empty file is a valid new SQLite database, and this way it is never readable by
    /// others, not even briefly). Not <c>:memory:</c> or a URI.
    /// </summary>
    public static void EnsureCreated(string databasePath)
    {
        if (!IsFile(databasePath) || File.Exists(databasePath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.ReadWrite };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerOnly;
        }

        try
        {
            new FileStream(databasePath, options).Dispose();
        }
        catch (IOException) when (File.Exists(databasePath))
        {
            // Created by another process in the meantime.
        }
    }

    /// <summary>
    /// Takes group and other access away from the database file and its <c>-wal</c> and <c>-shm</c> files where they exist, for an install that
    /// created them under a looser umask. The result says which files it changed and which it could not (a file that belongs to another user, a read-only
    /// mount), so the caller can warn: a file that stays readable by others with no message is the one thing this must not do. Nothing on Windows.
    /// </summary>
    public static RestrictResult Restrict(string databasePath)
    {
        if (OperatingSystem.IsWindows() || !IsFile(databasePath))
        {
            return RestrictResult.None;
        }

        return RestrictUnix(databasePath);
    }

    [UnsupportedOSPlatform("windows")]
    private static RestrictResult RestrictUnix(string databasePath)
    {
        List<string> changed = [];
        List<RestrictFailure> failed = [];
        foreach (var suffix in Suffixes)
        {
            var path = databasePath + suffix;
            UnixFileMode mode;
            try
            {
                if (!File.Exists(path) || (mode = File.GetUnixFileMode(path) & ModeBits) == 0 || (mode & Others) == 0)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Gone since the check: a checkpoint removed the WAL.
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(new RestrictFailure(path, 0, ex.Message));
                continue;
            }

            try
            {
                File.SetUnixFileMode(path, OwnerOnly);
                changed.Add(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Gone since the check.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(new RestrictFailure(path, mode, ex.Message));
            }
        }

        return new RestrictResult(changed, failed);
    }

    private static bool IsFile(string databasePath) =>
        !string.IsNullOrWhiteSpace(databasePath) && databasePath != ":memory:" && !databasePath.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What <see cref="PrivateSqliteFile.Restrict"/> did.</summary>
/// <param name="Changed">The files it restricted to their owner.</param>
/// <param name="Failed">The files others can read and it could not change.</param>
public sealed record RestrictResult(IReadOnlyList<string> Changed, IReadOnlyList<RestrictFailure> Failed)
{
    internal static RestrictResult None { get; } = new([], []);
}

/// <summary>A file that stays readable by others.</summary>
/// <param name="Path">The file.</param>
/// <param name="Mode">Its mode (0 when even that could not be read).</param>
/// <param name="Reason">Why it could not be changed.</param>
public sealed record RestrictFailure(string Path, UnixFileMode Mode, string Reason);
