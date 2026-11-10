using System.CommandLine;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

using Paduraksa.Core;
using Paduraksa.Core.Vault;
using Paduraksa.Server.Configuration;
using Paduraksa.Server.Hosting;
using Paduraksa.Vault;

namespace Paduraksa.Server.Cli;

/// <summary>
/// <c>paduraksa vault ...</c>. Today: <c>rotate-kek</c>, which re-wraps every key in <c>vault.db</c> under a new
/// key-encryption key.
/// </summary>
/// <remarks>
/// <b>The new key never comes through the command line</b> (argv shows in process listings and shell history): it is read from a
/// file (<c>--new-key-file path</c>) or standard input (<c>--new-key-file -</c>, refused when that is a terminal), as base64 of
/// 32 bytes, e.g. <c>head -c 32 /dev/urandom | base64</c>. It is held in pinned arrays, zeroed after use, never printed or
/// logged; a key file other users can read is used but warned about.
///
/// <b>The order that makes a crash safe</b> (<see cref="IVaultKekRotation"/>): the new key is stored durably before the
/// rotation, the rotation commits in one transaction, and only then is the old key retired. With the generated
/// <c>master.key</c> the command does the storing itself: the old file becomes <c>master.key.old</c> (0600, flushed), the new
/// key is written to <c>master.key.new</c> (also 0600 and flushed) and moved over <c>master.key</c> atomically, the directory
/// is flushed, and a failed rotation puts the old file back. A crash at any point leaves the key the database belongs to in
/// <c>master.key</c> or beside it. <c>master.key.old</c> stays until the operator deletes it, once the service has started
/// with the new key: it still opens old copies of <c>vault.db</c>. With a key file or the environment as the source nothing
/// can be stored there, so the new key must come from a file the operator keeps and the command says what to change
/// afterwards.
///
/// <b>Stop the service first.</b> A running process keeps the old key in memory and can no longer open the keys once they are
/// wrapped under the new one; it notices at its next write and must be restarted with the new key. A locked database is
/// reported, not waited for forever.
/// </remarks>
internal static class VaultCommands
{
    /// <summary>The name of the key file the vault generates in the data directory.</summary>
    internal const string GeneratedKeyFileName = "master.key";

    private const int MaxKeyChars = 1024;

    public static Command Build()
    {
        Command vault = new("vault", "Manage the key vault.");
        Option<string> newKeyFile = new("--new-key-file")
        {
            Description = "A file holding the new master key (base64 of 32 bytes), or - to read it from standard input. Never pass the key itself on the command line.",
            Required = true,
        };
        Command rotate = new("rotate-kek", "Re-wrap every key in the vault under a new master key. Stop the service first, and restart it with the new key afterwards.");
        rotate.Options.Add(newKeyFile);
        rotate.SetAction((parseResult, cancellationToken) => RotateKekAsync(
            hostOptions: null,
            parseResult.GetRequiredValue(newKeyFile),
            Console.In,
            standardInputIsTerminal: !Console.IsInputRedirected,
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error,
            cancellationToken));
        vault.Subcommands.Add(rotate);
        return vault;
    }

    /// <summary>
    /// Runs <c>vault rotate-kek</c>. Returns <see cref="ExitCodes.Success"/>, <see cref="ExitCodes.Failure"/> (the rotation could not be
    /// done, with the reason on <paramref name="error"/>; nothing changed unless it says otherwise) or
    /// <see cref="ExitCodes.InvalidConfiguration"/>.
    /// </summary>
    internal static async Task<int> RotateKekAsync(
        PaduraksaHostOptions? hostOptions,
        string newKeyFile,
        TextReader standardInput,
        bool standardInputIsTerminal,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newKeyFile);
        ArgumentNullException.ThrowIfNull(standardInput);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        PaduraksaOptions options;
        try
        {
            options = PaduraksaHost.LoadOptions(hostOptions);
        }
        catch (OptionsValidationException ex)
        {
            await error.WriteLineAsync("Invalid configuration:").ConfigureAwait(false);
            foreach (var failure in ex.Failures)
            {
                await error.WriteLineAsync("  " + failure).ConfigureAwait(false);
            }

            return ExitCodes.InvalidConfiguration;
        }

        char[]? newKey = null;
        try
        {
            var fromStandardInput = newKeyFile == "-";
            if (fromStandardInput && standardInputIsTerminal)
            {
                await error.WriteLineAsync("Standard input is a terminal: the key would be typed in the clear. Pipe it in or use --new-key-file <path>. Nothing changed.").ConfigureAwait(false);
                return ExitCodes.Failure;
            }

            newKey = fromStandardInput ? ReadKey(standardInput) : ReadKey(newKeyFile);
            if (!MasterKeyFormat.IsValid(newKey))
            {
                // Checked (with the vault's own parser) before anything is stored: a key file that isn't one must never replace master.key.
                await error.WriteLineAsync("The new master key must be base64 of exactly 32 bytes, not all zeros. Nothing changed.").ConfigureAwait(false);
                return ExitCodes.Failure;
            }

            if (!fromStandardInput && IsAccessibleToOthers(newKeyFile))
            {
                await error.WriteLineAsync($"Warning: {newKeyFile} can be read by other users. Restrict it (chmod 600) and delete it once the new key is in place.").ConfigureAwait(false);
            }

            await using var services = new ServiceCollection()
                .AddPaduraksaVault(options.DataDirectory)
                .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

            // The old key is resolved (and the database opened with it) before anything is stored: it stays in memory throughout.
            var status = await services.GetRequiredService<IVaultLifecycle>().InitializeAsync(cancellationToken).ConfigureAwait(false);

            string? backup = null;
            var keyPath = Path.Combine(options.DataDirectory, GeneratedKeyFileName);
            if (status.KekSource == KekSourceKind.GeneratedFile)
            {
                backup = StoreNewGeneratedKey(keyPath, newKey);
            }
            else if (fromStandardInput)
            {
                await error.WriteLineAsync(
                    "The master key comes from " + (status.KekSource == KekSourceKind.File ? "PADURAKSA_MASTER_KEY_FILE" : "PADURAKSA_MASTER_KEY")
                    + ", which this command can't update, so the new key must be in a file you keep (--new-key-file <path>), not standard input. Nothing changed.").ConfigureAwait(false);
                return ExitCodes.Failure;
            }

            KekRotationResult result;
            try
            {
                result = await services.GetRequiredService<IVaultKekRotation>().RotateKekAsync(newKey, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (backup is not null)
                {
                    // Nothing was committed: the old key goes back where it was.
                    File.Move(backup, keyPath, overwrite: true);
                    FlushDirectory(options.DataDirectory);
                }

                throw;
            }

            await output.WriteLineAsync(
                $"Rotated the master key: {result.Secrets} secret(s) and {result.Keys} key(s) re-wrapped "
                + $"({result.AccountKeys} account, {result.CertificateKeys} certificate, {result.StagedKeys} staged, {result.DataProtectionKeys} Data Protection).").ConfigureAwait(false);
            if (backup is not null)
            {
                await output.WriteLineAsync(
                    $"The new key is in {keyPath}; the previous one is kept in {backup}. Start the service, check it, then delete {backup}: it still opens old copies of vault.db.").ConfigureAwait(false);
            }
            else
            {
                var where = status.KekSource == KekSourceKind.File ? "the file named by PADURAKSA_MASTER_KEY_FILE" : "PADURAKSA_MASTER_KEY";
                await output.WriteLineAsync(
                    $"Now make the new key the master key: put it in {where}, then start the service. It can't open the vault with the old key any more.").ConfigureAwait(false);
            }

            return ExitCodes.Success;
        }
        catch (SqliteException ex)
        {
            await error.WriteLineAsync($"Couldn't open the vault database (is the service still running? stop it first): {ex.Message}").ConfigureAwait(false);
            return ExitCodes.Failure;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            // The vault's messages name the problem and never the key.
            await error.WriteLineAsync("Couldn't rotate the master key: " + ex.Message).ConfigureAwait(false);
            return ExitCodes.Failure;
        }
        finally
        {
            if (newKey is not null)
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(newKey.AsSpan()));
            }
        }
    }

    /// <summary>
    /// Keeps the current <c>master.key</c> as <c>master.key.old</c> and replaces it with the new key, atomically, with every file
    /// created 0600 and flushed to disk (and the directory after each rename-like step). Returns the backup's path.
    /// </summary>
    private static string StoreNewGeneratedKey(string keyPath, char[] newKey)
    {
        var backup = keyPath + ".old";
        var temporary = keyPath + ".new";
        if (File.Exists(backup))
        {
            throw new InvalidOperationException($"{backup} exists: it is the previous key of an earlier rotation. Check that the service works with the current key, then delete it, and run this again.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(keyPath))!;
        var old = GC.AllocateArray<byte>(MaxKeyChars + 1, pinned: true);
        var fresh = GC.AllocateArray<byte>(Encoding.ASCII.GetByteCount(newKey) + 1, pinned: true);
        try
        {
            int oldLength;
            using (FileStream source = new(keyPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0))
            {
                oldLength = source.ReadAtLeast(old, old.Length, throwOnEndOfStream: false);
            }

            if (oldLength > MaxKeyChars)
            {
                throw new InvalidOperationException($"{keyPath} is too large to be a key.");
            }

            WriteDurably(backup, old.AsSpan(0, oldLength));
            try
            {
                Encoding.ASCII.GetBytes(newKey, fresh);
                fresh[^1] = (byte)'\n';

                // A stale temporary of an interrupted run is replaced, never trusted: the new file is created exclusively.
                File.Delete(temporary);
                WriteDurably(temporary, fresh);
                File.Move(temporary, keyPath, overwrite: true);
                FlushDirectory(directory);
                return backup;
            }
            catch
            {
                File.Delete(temporary);
                File.Delete(backup);
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(old);
            CryptographicOperations.ZeroMemory(fresh);
        }
    }

    /// <summary>Creates <paramref name="path"/> (which must not exist) with mode 0600 from the first byte, writes <paramref name="bytes"/> unbuffered and flushes them to disk.</summary>
    private static void WriteDurably(string path, ReadOnlySpan<byte> bytes)
    {
        FileStreamOptions streamOptions = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 0 };
        if (!OperatingSystem.IsWindows())
        {
            streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using FileStream stream = new(path, streamOptions);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Flushes a directory's entries to disk (best effort: Windows and some file systems don't allow opening a directory, and
    /// there the rename is as durable as the platform makes it).
    /// </summary>
    private static void FlushDirectory(string directory)
    {
        try
        {
            using var handle = File.OpenHandle(directory, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            RandomAccess.FlushToDisk(handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Not possible here; see the summary.
        }
    }

    // Stricter than the vault's check of a configured key file (which accepts group read, for Docker secrets mounted 0440): this file
    // is a hand-made, short-lived copy of the new key that should be readable by its owner only, and deleted afterwards.
    private static bool IsAccessibleToOthers(string path) =>
        !OperatingSystem.IsWindows()
        && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0;

    private static char[] ReadKey(string path)
    {
        var bytes = GC.AllocateArray<byte>(MaxKeyChars + 1, pinned: true);
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0);
            var length = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (length > MaxKeyChars)
            {
                throw new InvalidOperationException($"{path} is too large to be a key.");
            }

            var chars = new char[length];
            try
            {
                Encoding.ASCII.GetChars(bytes.AsSpan(0, length), chars);
                return Trimmed(chars);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars.AsSpan()));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static char[] ReadKey(TextReader input)
    {
        var buffer = GC.AllocateArray<char>(MaxKeyChars + 1, pinned: true);
        try
        {
            var length = input.ReadBlock(buffer, 0, buffer.Length);
            if (length > MaxKeyChars)
            {
                throw new InvalidOperationException("The key on standard input is too large to be a key.");
            }

            return Trimmed(buffer.AsSpan(0, length));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(buffer.AsSpan()));
        }
    }

    private static char[] Trimmed(ReadOnlySpan<char> text) => GCPinned(text.Trim());

    private static char[] GCPinned(ReadOnlySpan<char> text)
    {
        var copy = GC.AllocateArray<char>(text.Length, pinned: true);
        text.CopyTo(copy);
        return copy;
    }
}
