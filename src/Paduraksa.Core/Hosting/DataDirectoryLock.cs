namespace Paduraksa.Core.Hosting;

/// <summary>The data directory is already in use by a running server.</summary>
public sealed class DataDirectoryInUseException(string message) : Exception(message);

/// <summary>
/// The server's claim on its data directory: an exclusive lock on <c>&lt;data dir&gt;/run/server.lock</c>, held for the process's life.
/// One data directory serves one server (docs/design/vault-and-certificates.md "One server per data directory"), so a second server fails to start, and <c>paduraksa restore</c>, which replaces
/// the databases, refuses to run while one is up.
/// </summary>
/// <remarks>
/// It is the file system's own lock (<c>flock</c> on Unix, a share mode on Windows), so it is released by the operating system when the
/// process ends however it ends: no stale lock can outlive a crash, and no pid in a file has to be believed. The file stays after the
/// process (deleting it would race with the next claimant). Where the file system can't lock (some network mounts), the claim silently
/// isn't enforced, which is the behaviour before this existed.
/// </remarks>
public sealed class DataDirectoryLock : IDisposable
{
    /// <summary>The lock file's name inside <c>run</c>.</summary>
    public const string FileName = "server.lock";

    private readonly FileStream _stream;

    private DataDirectoryLock(FileStream stream, bool enforced)
    {
        _stream = stream;
        Enforced = enforced;
    }

    /// <summary>
    /// False when the file system does not actually lock (.NET ignores a failing <c>flock</c>, e.g. ENOLCK or EOPNOTSUPP on a network mount),
    /// so the claim holds nothing: another server, or a restore, would not be kept out. Found by trying to open the file a second time, which a
    /// working lock refuses.
    /// </summary>
    public bool Enforced { get; }

    /// <summary>Claims <paramref name="dataDirectory"/>, or null if another process holds it.</summary>
    /// <exception cref="IOException">The run directory or the file can't be created.</exception>
    public static DataDirectoryLock? TryAcquire(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var run = Path.Combine(dataDirectory, "run");
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(run);
        }
        else
        {
            Directory.CreateDirectory(run, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        FileStreamOptions options = new() { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None, BufferSize = 0 };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            var stream = new FileStream(Path.Combine(run, FileName), options);
            return new DataDirectoryLock(stream, enforced: RefusesSecondOpen(Path.Combine(run, FileName), options));
        }
        catch (IOException) when (File.Exists(Path.Combine(run, FileName)))
        {
            // The file exists and can't be opened exclusively: somebody holds it.
            return null;
        }
    }

    private static bool RefusesSecondOpen(string path, FileStreamOptions options)
    {
        try
        {
            new FileStream(path, options).Dispose();
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>Claims <paramref name="dataDirectory"/>.</summary>
    /// <exception cref="DataDirectoryInUseException">Another process holds it.</exception>
    public static DataDirectoryLock Acquire(string dataDirectory) =>
        TryAcquire(dataDirectory)
        ?? throw new DataDirectoryInUseException(
            $"The data directory {dataDirectory} is in use by another Paduraksa process. Stop it first, or give this process its own data directory.");

    public void Dispose() => _stream.Dispose();
}
