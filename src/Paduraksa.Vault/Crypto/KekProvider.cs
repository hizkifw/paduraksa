using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Paduraksa.Core.Vault;

namespace Paduraksa.Vault.Crypto;

/// <summary>A resolved KEK and where it came from. The vault runtime owns (and disposes) <see cref="Kek"/>.</summary>
/// <param name="Kek">The key.</param>
/// <param name="Source">Which of the three sources supplied it.</param>
/// <param name="BesideDatabase">The key file sits next to <c>vault.db</c> (see <see cref="VaultStatus.KekBesideDatabase"/>).</param>
internal sealed record ResolvedKek(Kek Kek, KekSourceKind Source, bool BesideDatabase);

/// <summary>
/// Finds the KEK (PLAN §8.3), in this priority order: the file named by <c>PADURAKSA_MASTER_KEY_FILE</c>, the
/// <c>PADURAKSA_MASTER_KEY</c> variable, else <c>master.key</c> in the data directory (generated on first start).
/// </summary>
/// <remarks>
/// <para>
/// <b>The first source that is set wins, and a source that is set but wrong is an error, never a fall-through</b>: a typo
/// in the file path must not silently start a vault under a different key. (When both variables are set the variable is
/// ignored, with a warning.) Text form: see <see cref="Kek"/>.
/// </para>
/// <para>
/// <b>master.key.</b> Generated only if it does not exist, with an exclusive create (<c>CreateNew</c>, mode 0600 from the
/// first byte, flushed): of any number of processes racing at the first start one wins and the others read the winner's
/// file; an existing file is never replaced, and a failed write removes the half-written file. It is also not generated when
/// <c>vault.db</c> already holds data, since a new key could not open it: that is an error that says so (a lost
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
internal sealed partial class KekProvider(ILogger<KekProvider> logger)
{
    public const string KeyVariable = "PADURAKSA_MASTER_KEY";

    public const string KeyFileVariable = "PADURAKSA_MASTER_KEY_FILE";

    public const string GeneratedFileName = "master.key";

    /// <summary>A key file is 44 characters and a newline; anything much larger is not one, and is not read.</summary>
    private const int MaxKeyFileBytes = 1024;

    private const UnixFileMode OthersAny = UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private const UnixFileMode GroupAny = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute;

    /// <summary>Resolves the KEK for the vault whose database is <paramref name="databasePath"/> in <paramref name="dataDirectory"/>.</summary>
    /// <exception cref="InvalidOperationException">The chosen source is missing, malformed or unsafe.</exception>
    public ResolvedKek Resolve(Func<string, string?> getVariable, string dataDirectory, string databasePath)
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
        return new(LoadOrGenerate(generatedPath, dataDirectory, databasePath), KekSourceKind.GeneratedFile, true);
    }

    /// <summary>
    /// Writes <paramref name="kek"/> to <paramref name="path"/> if nothing is there yet (see the type's remarks).
    /// Returns false, having written nothing, when the file already exists.
    /// </summary>
    /// <remarks>
    /// Creation is <c>FileMode.CreateNew</c> straight onto the final path, mode 0600 from the first byte, and the data is
    /// flushed before returning. A temp file and a rename would never show a reader a partial file, but .NET's
    /// <c>File.Move(overwrite: false)</c> is not atomic on Unix (it was measured replacing a file a racing creator had just
    /// moved into place), so the exclusive create is the only primitive that cannot overwrite; readers wait for the
    /// 45 bytes instead (<see cref="WaitForWriter"/>). The file's data is fsynced but its directory entry is not: .NET cannot open
    /// a directory to fsync it, so after a power loss right after the very first start the file may be missing. That can only
    /// cost a vault that holds nothing yet (a missing <c>master.key</c> beside a <c>vault.db</c> that holds data is refused,
    /// not regenerated), and the error then says what to do.
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

        // BufferSize 0: FileStream must not keep a buffer of its own holding the key text after it is disposed.
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 0 };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        FileStream stream;
        try
        {
            // CreateNew is O_EXCL: of any number of racing creators exactly one gets the file, and nothing existing is opened.
            stream = new FileStream(path, options);
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }

        var text = GC.AllocateArray<byte>(Kek.FileLength, pinned: true);
        var complete = false;
        try
        {
            kek.WriteEncoded(text);
            text[Kek.TextLength] = (byte)'\n';
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
            CryptographicOperations.ZeroMemory(text);
            if (!complete)
            {
                // Never leave half a key behind: the next start would find a file that is not one.
                File.Delete(path);
            }
        }
    }

    private Kek LoadOrGenerate(string path, string dataDirectory, string databasePath)
    {
        if (File.Exists(path))
        {
            WaitForWriter(path);
            return ReadFile(path, GeneratedFileName, generated: true);
        }

        if (File.Exists(databasePath) && new FileInfo(databasePath).Length > 0)
        {
            throw new InvalidOperationException(
                $"The vault database {databasePath} exists but there is no master key: set {KeyFileVariable} or {KeyVariable}, or restore {path} from a backup. "
                + "A new key is not generated, since it could not open the existing secrets (if the database holds none, delete it).");
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
        WaitForWriter(path);
        return ReadFile(path, GeneratedFileName, generated: true);
    }

    /// <summary>
    /// The file is created in place (that is what makes creation exclusive), so a concurrent start can see it before its
    /// key is written. Waits (at most a second) for the 45 bytes to arrive instead of reading half a key.
    /// </summary>
    private static void WaitForWriter(string path)
    {
        for (var attempt = 0; attempt < 20 && new FileInfo(path).Length < Kek.FileLength; attempt++)
        {
            Thread.Sleep(50);
        }
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
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 0);
                CheckMode(stream, path, generated);
                read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
