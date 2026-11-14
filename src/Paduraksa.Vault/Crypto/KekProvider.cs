using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Paduraksa.Core.Vault;

namespace Paduraksa.Vault.Crypto;

/// <summary>A resolved KEK and where it came from. The vault runtime owns (and disposes) <see cref="Kek"/>.</summary>
internal sealed record ResolvedKek(Kek Kek, KekSourceKind Source, bool BesideDatabase);

/// <summary>
/// Finds the KEK, in this priority order: the file named by <c>PADURAKSA_MASTER_KEY_FILE</c>, the
/// <c>PADURAKSA_MASTER_KEY</c> variable, else <c>master.key</c> in the data directory (generated on first start).
/// </summary>
/// <remarks>
/// <para>
/// <b>The first source that is set wins, and a source that is set but wrong is an error, never a fall-through</b>: a typo
/// in the file path must not silently start a vault under a different key. (When both variables are set the variable is
/// ignored, with a warning.) Text form: see <see cref="Kek"/>.
/// </para>
/// <para>
/// <b>master.key.</b> Generated only if it does not exist. The complete key is written to a private temp file (mode 0600 from
/// the first byte, flushed) and then given the final name with an atomic hard link that fails instead of replacing: of any
/// number of processes racing at the first start one wins, the others read the winner's complete file, and an existing file is
/// never replaced. A failed write removes the temp file. Readers that arrive while another writer still holds the file
/// exclusively retry for a bounded time, and a generated file is only read once it holds the whole key. It is also not
/// generated when <c>vault.db</c> already holds data, since a new key could not open it: that is an error that says so (a lost
/// <c>master.key</c> is recoverable from a backup, a silently replaced one is not).
/// </para>
/// <para>
/// <b>Permissions (Unix).</b> An existing <c>master.key</c> that others can read, write or execute is <i>refused</i>: the
/// vault made the file 0600 itself, so any wider mode means somebody changed it, and a world-readable KEK next to the
/// database defeats the encryption. Group access only warns, since a shared service group is a legitimate setup. An
/// explicit key file is never refused for its mode, only warned about when it is world-readable, because Docker secrets and
/// systemd credentials are routinely mounted 0444 or 0440 and refusing them would make the feature unusable.
/// </para>
/// </remarks>
internal sealed partial class KekProvider(ILogger<KekProvider> logger, int keyReadWaitMilliseconds)
{
    public KekProvider(ILogger<KekProvider> logger)
        : this(logger, KeyReadWaitMilliseconds)
    {
    }

    public const string KeyVariable = "PADURAKSA_MASTER_KEY";

    public const string KeyFileVariable = "PADURAKSA_MASTER_KEY_FILE";

    public const string GeneratedFileName = "master.key";

    /// <summary>A key file is 44 characters and a newline; anything much larger is not one, and is not read.</summary>
    private const int MaxKeyFileBytes = 1024;

    /// <summary>How long a reader waits for another first start to finish writing <c>master.key</c>. Tests that hold the
    /// file on purpose pass a longer wait: on a loaded machine their own delays can outlast this one.</summary>
    private const int KeyReadWaitMilliseconds = 2000;

    private const int KeyReadPollMilliseconds = 20;

    /// <summary><c>EEXIST</c>, from <c>link(2)</c>: the final name is already taken, so another creator won.</summary>
    private const int LinkExists = 17;

    /// <summary><c>HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION)</c>, Windows' report of a conflicting share.</summary>
    private const int WindowsSharingViolation = unchecked((int)0x80070020);

    private const UnixFileMode OthersAny = UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private const UnixFileMode GroupAny = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute;

    /// <summary>Resolves the KEK for the vault whose database is <paramref name="databasePath"/> in <paramref name="dataDirectory"/>.</summary>
    /// <exception cref="InvalidOperationException">The chosen source is missing, malformed or unsafe.</exception>
    public ResolvedKek Resolve(Func<string, string?> getVariable, string dataDirectory, string databasePath) =>
        Resolve(getVariable, dataDirectory, databasePath, generate: true)!; // Only generate: false answers null.

    /// <summary>
    /// <see cref="Resolve(Func{string, string?}, string, string)"/> for a read-only look: with <paramref name="generate"/> false, a data directory
    /// that has no <c>master.key</c> and no source set answers null (a first start would generate one) instead of writing it.
    /// </summary>
    public ResolvedKek? Resolve(Func<string, string?> getVariable, string dataDirectory, string databasePath, bool generate)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        var filePath = getVariable(KeyFileVariable);
        var encoded = getVariable(KeyVariable);

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            if (!string.IsNullOrWhiteSpace(encoded))
            {
                LogVariableIgnored(KeyVariable, KeyFileVariable);
            }

            var path = Path.GetFullPath(filePath.Trim());
            return new(ReadFile(path, KeyFileVariable, generated: false), KekSourceKind.File, IsBeside(path, dataDirectory));
        }

        if (!string.IsNullOrWhiteSpace(encoded))
        {
            try
            {
                return new(Kek.Parse(encoded), KekSourceKind.Environment, false);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException($"{KeyVariable} is not usable: {ex.Message}", ex);
            }
        }

        var generatedPath = Path.GetFullPath(Path.Combine(dataDirectory, GeneratedFileName));
        if (!generate && !File.Exists(generatedPath))
        {
            return HoldsData(databasePath) ? throw new InvalidOperationException(MissingKeyMessage(generatedPath, databasePath)) : null;
        }

        return new(LoadOrGenerate(generatedPath, dataDirectory, databasePath), KekSourceKind.GeneratedFile, true);
    }

    /// <summary>Where a key file is, for a read-only look: <c>PADURAKSA_MASTER_KEY_FILE</c>'s, else (no variable set) the data directory's generated one.</summary>
    public static string? KeyFilePath(Func<string, string?> getVariable, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        if (getVariable(KeyFileVariable) is { } file && !string.IsNullOrWhiteSpace(file))
        {
            return Path.GetFullPath(file.Trim());
        }

        return string.IsNullOrWhiteSpace(getVariable(KeyVariable)) ? Path.GetFullPath(Path.Combine(dataDirectory, GeneratedFileName)) : null;
    }

    private static bool HoldsData(string databasePath) => File.Exists(databasePath) && new FileInfo(databasePath).Length > 0;

    private static string MissingKeyMessage(string path, string databasePath) =>
        $"The vault database {databasePath} exists but there is no master key: set {KeyFileVariable} or {KeyVariable}, or restore {path} from a backup. "
        + "A new key is not generated, since it could not open the existing secrets (if the database holds none, delete it).";

    /// <summary>
    /// Writes <paramref name="kek"/> to <paramref name="path"/> if nothing is there yet (see the type's remarks).
    /// Returns false, having written nothing, when the file already exists.
    /// </summary>
    /// <remarks>
    /// The key is written whole to a private temp file (0600 from the first byte) and only then installed at
    /// <paramref name="path"/>: with a hard link, which is atomic and fails with <c>EEXIST</c> instead of replacing anything,
    /// so a reader sees either no file or a complete one and racing first starts cannot overwrite each other. .NET's
    /// <c>File.Move(overwrite: false)</c> is not used because it is not atomic on Unix (it was measured replacing a file a
    /// racing creator had just moved into place). On a file system without hard links the install falls back to an exclusive
    /// create on the final path; readers then wait for the 45 bytes instead (<see cref="WaitForComplete"/>). The file's data is
    /// fsynced but its directory entry is not: .NET cannot open a directory to fsync it, so after a power loss right after the
    /// very first start the file may be missing. That can only cost a vault that holds nothing yet (a missing
    /// <c>master.key</c> beside a <c>vault.db</c> that holds data is refused, not regenerated), and the error then says what to do.
    /// </remarks>
    internal static bool TryCreateKeyFile(string path, Kek kek)
    {
        var directory = Path.GetDirectoryName(path) ?? ".";
        if (!Directory.Exists(directory))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        var text = GC.AllocateArray<byte>(Kek.FileLength, pinned: true);
        // Dot-prefixed and unique: two creators never share a temp file, and a crash leaves no readable key behind.
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(8))}.tmp");
        try
        {
            kek.WriteEncoded(text);
            text[Kek.TextLength] = (byte)'\n';

            // BufferSize 0: FileStream must not keep a buffer of its own holding the key text after it is disposed.
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 0 };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(temp, options))
            {
                stream.Write(text);
                stream.Flush(flushToDisk: true);
            }

            return InstallExclusive(temp, path, text);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(text);
            TryDelete(temp);
        }
    }

    /// <summary>Installs the finished temp file at <paramref name="path"/> without ever replacing an existing file.</summary>
    private static bool InstallExclusive(string temp, string path, ReadOnlySpan<byte> text)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                File.Move(temp, path, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(path))
            {
                return false;
            }
        }

        switch (TryLink(temp, path))
        {
            case true:
                return true;
            case false:
                return false; // The name is taken: another first start won.
            default:
                return CreateExclusive(path, text); // No hard links here.
        }
    }

    /// <summary>
    /// <c>link(2)</c>: atomically gives the finished temp file the final name, or fails with <c>EEXIST</c>.
    /// Null means this file system has no hard links, so the caller falls back to an exclusive create.
    /// </summary>
    private static bool? TryLink(string temp, string path)
    {
        try
        {
            if (Link(temp, path) == 0)
            {
                return true;
            }

            return Marshal.GetLastPInvokeError() == LinkExists ? false : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The no-hard-links fallback: an exclusive <c>O_EXCL</c> create of the final path, then the whole key.</summary>
    private static bool CreateExclusive(string path, ReadOnlySpan<byte> text)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 0 };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        FileStream stream;
        try
        {
            stream = new FileStream(path, options);
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }

        var complete = false;
        try
        {
            using (stream)
            {
                stream.Write(text);
                stream.Flush(flushToDisk: true);
            }

            complete = true;
            return true;
        }
        finally
        {
            if (!complete)
            {
                // Never leave half a key behind: the next start would find a file that is not one.
                TryDelete(path);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; a leftover temp file is dot-prefixed and never read as a key.
        }
    }

    private Kek LoadOrGenerate(string path, string dataDirectory, string databasePath)
    {
        if (File.Exists(path))
        {
            return ReadFile(path, GeneratedFileName, generated: true);
        }

        if (HoldsData(databasePath))
        {
            throw new InvalidOperationException(MissingKeyMessage(path, databasePath));
        }

        var kek = Kek.Generate();
        try
        {
            if (TryCreateKeyFile(path, kek))
            {
                LogGenerated(path);
                return kek;
            }
        }
        catch
        {
            kek.Dispose();
            throw;
        }

        // Another process created it between our check and our create: its key is the key.
        kek.Dispose();
        return ReadFile(path, GeneratedFileName, generated: true);
    }

    /// <summary>
    /// Opens a key file, retrying (at most <see cref="KeyReadWaitMilliseconds"/>) while a first start that holds the file
    /// exclusively is still writing it. On Linux .NET enforces <see cref="FileShare"/> with advisory locks, so a reader that
    /// arrives while the creator's <see cref="FileShare.None"/> handle is open fails at once instead of waiting.
    /// </summary>
    private FileStream OpenKeyFile(string path, bool generated)
    {
        var started = Environment.TickCount64;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 0);
            }
            catch (IOException ex) when (generated && IsSharingViolation(ex) && Environment.TickCount64 - started < keyReadWaitMilliseconds)
            {
                Thread.Sleep(KeyReadPollMilliseconds);
            }
        }
    }

    /// <summary>
    /// Waits (at most <see cref="KeyReadWaitMilliseconds"/>) for a key file to be complete. The hard-link install never
    /// exposes a partial one; this covers the exclusive-create fallback and a key file some other writer is producing.
    /// </summary>
    private void WaitForComplete(FileStream stream)
    {
        var started = Environment.TickCount64;
        while (stream.Length < Kek.FileLength && Environment.TickCount64 - started < keyReadWaitMilliseconds)
        {
            Thread.Sleep(KeyReadPollMilliseconds);
        }
    }

    /// <summary>
    /// Whether <paramref name="ex"/> is the BCL's report of a conflicting <see cref="FileShare"/> lock: <c>ERROR_SHARING_VIOLATION</c>
    /// on Windows, the Unix <c>EAGAIN</c>/<c>EWOULDBLOCK</c> the advisory lock returns (64-bit Linux and macOS values).
    /// </summary>
    private static bool IsSharingViolation(IOException ex) => ex.HResult is WindowsSharingViolation or 11 or 35;

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

    /// <summary>
    /// After a start failed because the key does not match the database: the name of the <c>master.key.old</c> or
    /// <c>master.key.new</c> of an interrupted <c>vault rotate-kek</c> that does hold the key the database belongs to, or null.
    /// </summary>
    public static string? FindMatchingBackup(string dataDirectory, ReadOnlySpan<byte> storedCheck)
    {
        foreach (var name in new[] { GeneratedFileName + ".old", GeneratedFileName + ".new" })
        {
            var buffer = GC.AllocateArray<byte>(MaxKeyFileBytes + 1, pinned: true);
            try
            {
                using var stream = new FileStream(Path.Combine(dataDirectory, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 0);
                var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                var chars = new char[read];
                try
                {
                    System.Text.Encoding.ASCII.GetChars(buffer.AsSpan(0, read), chars);
                    using var kek = Kek.Parse(chars);
                    if (kek.Matches(storedCheck))
                    {
                        return name;
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(chars.AsSpan()));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                // Not there, not readable or not a key: not the one.
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }

        return null;
    }

    private Kek ReadFile(string path, string what, bool generated)
    {
        var buffer = GC.AllocateArray<byte>(MaxKeyFileBytes + 1, pinned: true);
        try
        {
            int read;
            try
            {
                // Opened first, and the mode judged on the open handle: a check on the path and then an open could be
                // fooled by the file being swapped in between. BufferSize 0, as when writing.
                using var stream = OpenKeyFile(path, generated);
                if (generated)
                {
                    WaitForComplete(stream);
                }

                CheckMode(stream, path, generated);
                read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            }
            catch (UnauthorizedAccessException ex)
            {
                // The commonest first-deploy trap: a Docker (non-swarm) secret is a bind mount with the host's owner and
                // mode, so a host-root 0600 file is unreadable for the container's uid 10001.
                throw new InvalidOperationException(
                    $"The master key file {path} ({what}) cannot be read by {CurrentUser()}: {ex.Message} "
                    + "This is a permissions problem, not a missing key. A Docker bind-mounted secret keeps its host owner and mode: "
                    + $"run sudo chown 10001:10001 '{path}' && sudo chmod 400 '{path}' (the container runs as uid 10001).",
                    ex);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException($"The master key file {path} ({what}) cannot be read: {ex.Message}", ex);
            }

            if (read > MaxKeyFileBytes)
            {
                throw new InvalidOperationException($"The master key file {path} ({what}) is too large to be a key.");
            }

            Span<char> text = stackalloc char[MaxKeyFileBytes];
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] >= 0x80)
                {
                    throw NotUsable(path, what, generated, null);
                }

                text[i] = (char)buffer[i];
            }

            try
            {
                return Kek.Parse(text[..read]);
            }
            catch (FormatException ex)
            {
                throw NotUsable(path, what, generated, ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(text));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static InvalidOperationException NotUsable(string path, string what, bool generated, FormatException? inner) =>
        new(
            $"The master key file {path} ({what}) is not usable"
            + (inner is null ? "." : $": {inner.Message}")
            + (generated ? " If it is empty or cut short (a first start that was interrupted) and vault.db holds no secrets, delete it and the vault generates a new one." : string.Empty),
            inner);

    /// <summary>The effective user, with its uid on Unix (from <c>/proc/self/status</c>), for a permission error that says who was refused.</summary>
    private static string CurrentUser()
    {
        if (OperatingSystem.IsWindows())
        {
            return Environment.UserName;
        }

        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("Uid:", StringComparison.Ordinal))
                {
                    var fields = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length >= 2 && int.TryParse(fields[1], out var uid))
                    {
                        return $"{Environment.UserName} (uid {uid})";
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No procfs: the name alone still says who.
        }

        return Environment.UserName;
    }

    private void CheckMode(FileStream stream, string path, bool generated)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var mode = File.GetUnixFileMode(stream.SafeFileHandle);
        if (generated && (mode & OthersAny) != 0)
        {
            throw new InvalidOperationException(
                $"The master key file {path} is accessible to other users (mode {Convert.ToString((int)mode, 8)}), so the vault refuses to use it. Run: chmod 600 '{path}'");
        }

        if (!generated && (mode & UnixFileMode.OtherRead) != 0)
        {
            LogWorldReadable(path);
        }
        else if (generated && (mode & GroupAny) != 0)
        {
            LogGroupAccessible(path);
        }
    }

    /// <summary>
    /// Whether <paramref name="filePath"/> is in the data directory or below it, after resolving symbolic links on both
    /// sides (the file itself and every directory on the way), best effort.
    /// </summary>
    internal static bool IsBeside(string filePath, string dataDirectory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        try
        {
            var target = RealPath(Path.GetFullPath(filePath), 0);
            var directory = Path.GetDirectoryName(target) ?? ".";
            var relative = Path.GetRelativePath(RealPath(Path.GetFullPath(dataDirectory), 0), directory);

            // Beside means "." or a path that does not climb: not rooted (another drive) and no ".." segment at the start.
            return relative == "."
                || (!Path.IsPathRooted(relative)
                    && relative != ".."
                    && !relative.StartsWith(".." + Path.DirectorySeparatorChar, comparison)
                    && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, comparison));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The path with every symbolic link in it (any component, and chains of links) resolved; components that do not exist stay as written.</summary>
    private static string RealPath(string fullPath, int depth)
    {
        if (depth > 32)
        {
            throw new IOException("Too many levels of symbolic links.");
        }

        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } resolved)
            {
                current = RealPath(resolved.FullName, depth + 1);
            }
        }

        return current;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Generated a new master key at {Path} (mode 0600). Back it up separately from vault.db: without it the vault cannot be opened.")]
    private partial void LogGenerated(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Ignored} is set but {Used} takes priority, so it is ignored.")]
    private partial void LogVariableIgnored(string ignored, string used);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The master key file {Path} is readable by every user on this machine; restrict it to the account Paduraksa runs as.")]
    private partial void LogWorldReadable(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The master key file {Path} is accessible to its group; keep that group to the account Paduraksa runs as.")]
    private partial void LogGroupAccessible(string path);
}
