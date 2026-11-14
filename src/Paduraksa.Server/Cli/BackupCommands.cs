using System.CommandLine;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Paduraksa.Application.Backup;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Core.Backup;
using Paduraksa.Core.Hosting;
using Paduraksa.Core.Ids;
using Paduraksa.Core.Options;
using Paduraksa.Core.Vault;
using Paduraksa.Data;
using Paduraksa.Server.Configuration;
using Paduraksa.Server.Hosting;
using Paduraksa.Storage.Sqlite;
using Paduraksa.Vault;

namespace Paduraksa.Server.Cli;

/// <summary>
/// <c>paduraksa backup --out &lt;file&gt;</c> and <c>paduraksa restore &lt;file&gt;</c>: the full backup of docs/design/vault-and-certificates.md "Backup and restore (M10)". Both
/// work on the data directory directly, so they run in the container as <c>docker exec paduraksa paduraksa backup ...</c> and, with the
/// service stopped, <c>docker compose run --rm paduraksa restore ...</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The passphrase never comes through the command line</b> (argv shows in process listings and shell history). In this order: the file
/// named by <c>--passphrase-file</c> (<c>-</c> is standard input), the <c>PADURAKSA_BACKUP_PASSPHRASE</c> variable, else a prompt without
/// echo (twice for a backup). A file other users can read is used but warned about; a variable that is also set next to a file is ignored.
/// </para>
/// <para>
/// <b>Backup</b> is a read: the server may run. It needs the master key the way the server does (the vault opens its database to re-wrap
/// the keys for the archive), is audited as <c>backup.create</c> under the <c>cli</c> actor, and writes the archive to a private
/// temporary name beside <c>--out</c> and renames it, so a file at the final name is always a whole one. It never overwrites.
/// </para>
/// <para>
/// <b>Restore</b> refuses while a server holds the data directory (<see cref="DataDirectoryLock"/>), verifies the whole archive before
/// touching anything, moves the current databases to <c>pre-restore-&lt;time&gt;</c> beside them (nothing is deleted), installs the restored
/// ones, re-wraps the vault under this machine's master key (generating <c>master.key</c> when it has none, as a first start does) and
/// then checks what a start would: the vault opens under that key and the config database opens, migrates and has its revisions. Any
/// failure after the first move puts everything back.
/// </para>
/// </remarks>
internal static class BackupCommands
{
    /// <summary>The variable that can carry the passphrase (a variable is visible to the owner of the process only, unlike an argument).</summary>
    internal const string PassphraseVariable = "PADURAKSA_BACKUP_PASSPHRASE";

    private const int MaxPassphraseBytes = 4 * 1024;

    private static readonly string[] DatabaseFiles =
    [
        ConfigDatabase.FileName, ConfigDatabase.FileName + "-wal", ConfigDatabase.FileName + "-shm",
        "vault.db", "vault.db-wal", "vault.db-shm",
        "last-good.json",
    ];

    public static Command BuildBackup()
    {
        Option<string> output = new("--out")
        {
            Description = "Where to write the archive (e.g. /data/backups/paduraksa.pdkbak). It must not exist; its directory is created.",
            Required = true,
        };
        Option<string> passphraseFile = PassphraseFileOption();
        Command backup = new("backup", "Write a full, passphrase-encrypted backup of the config and the vault. The server may keep running.");
        backup.Options.Add(output);
        backup.Options.Add(passphraseFile);
        backup.SetAction((parseResult, cancellationToken) => BackupAsync(
            hostOptions: null,
            parseResult.GetRequiredValue(output),
            Input(parseResult.GetValue(passphraseFile)),
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error,
            cancellationToken));
        return backup;
    }

    public static Command BuildRestore()
    {
        Argument<string> file = new("file") { Description = "The archive to restore." };
        Option<string> passphraseFile = PassphraseFileOption();
        Command restore = new("restore", "Replace the config and the vault with a backup. Stop the server first; the current databases are kept in pre-restore-<time>.");
        restore.Arguments.Add(file);
        restore.Options.Add(passphraseFile);
        restore.SetAction((parseResult, cancellationToken) => RestoreAsync(
            hostOptions: null,
            parseResult.GetRequiredValue(file),
            Input(parseResult.GetValue(passphraseFile)),
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error,
            cancellationToken));
        return restore;
    }

    private static Option<string> PassphraseFileOption() => new("--passphrase-file")
    {
        Description = "A file holding the passphrase (its last line break is dropped), or - for standard input. Otherwise PADURAKSA_BACKUP_PASSPHRASE, otherwise a prompt. Never pass the passphrase on the command line.",
    };

    private static PassphraseInput Input(string? file) =>
        new(file, Environment.GetEnvironmentVariable, Console.In, StandardInputIsTerminal: !Console.IsInputRedirected, PromptHidden);

    /// <summary>Where a passphrase may come from, so the command's tests can supply each without a terminal.</summary>
    /// <param name="File">The <c>--passphrase-file</c> path, or <c>-</c> for standard input, or null.</param>
    /// <param name="GetVariable">Environment access.</param>
    /// <param name="StandardInput">Standard input.</param>
    /// <param name="StandardInputIsTerminal">Whether it is a terminal (a prompt is possible; a file named <c>-</c> is refused).</param>
    /// <param name="Prompt">Asks for a line without echoing it; null when the input ends.</param>
    internal sealed record PassphraseInput(string? File, Func<string, string?> GetVariable, TextReader StandardInput, bool StandardInputIsTerminal, Func<string, string?> Prompt);

    /// <summary>Makes a backup. Exit codes: <see cref="ExitCodes.Success"/>, <see cref="ExitCodes.Failure"/> (nothing was written at <paramref name="outPath"/>), <see cref="ExitCodes.InvalidConfiguration"/>.</summary>
    internal static async Task<int> BackupAsync(PaduraksaHostOptions? hostOptions, string outPath, PassphraseInput input, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outPath);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var (options, invalid) = await LoadOptionsAsync(hostOptions, error).ConfigureAwait(false);
        if (options is null)
        {
            return invalid;
        }

        var target = Path.GetFullPath(outPath);
        if (File.Exists(target) || Directory.Exists(target))
        {
            await error.WriteLineAsync($"{target} exists; a backup never overwrites. Choose another name.").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        if (!File.Exists(Path.Combine(options.DataDirectory, ConfigDatabase.FileName)))
        {
            await error.WriteLineAsync($"There is no config database in {options.DataDirectory}: nothing to back up (is Paduraksa:DataDirectory the one the server uses?).").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        var passphrase = await ReadPassphraseAsync(input, confirm: true, error).ConfigureAwait(false);
        if (passphrase is null)
        {
            return ExitCodes.Failure;
        }

        if (BackupPassphrase.Check(passphrase) is { } weak)
        {
            await error.WriteLineAsync(weak + " Nothing was written.").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        var partial = target + ".partial-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            LiveOptions<IdentityOptions> identity = options.Admin.ToIdentityOptions();
            await using var provider = new ServiceCollection()
                .AddPaduraksaDbContextFactory<ConfigDbContext>(Path.Combine(options.DataDirectory, ConfigDatabase.FileName))
                .AddPaduraksaVault(options.DataDirectory)
                .AddPaduraksaIdentity(options.DataDirectory, identity)
                .AddPaduraksaBackup(options.DataDirectory)
                .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            await ConfigDatabase.InitializeAsync(provider.GetRequiredService<IDbContextFactory<ConfigDbContext>>(), Int64OrderedIdGenerator.Shared, cancellationToken).ConfigureAwait(false);
            await CliSettings.ApplyStoredAsync(identity, options, cancellationToken).ConfigureAwait(false);

            BackupResult result;
            await using (var file = CreatePrivateFile(partial))
            {
                result = await provider.GetRequiredService<BackupService>().CreateAsync(CommandOrigin.Cli, passphrase, file, cancellationToken).ConfigureAwait(false);
                if (result.Succeeded)
                {
                    file.Flush(flushToDisk: true);
                }
            }

            if (!result.Succeeded)
            {
                File.Delete(partial);
                await error.WriteLineAsync($"{result.Problem} {result.Detail}".TrimEnd() + " Nothing was written.").ConfigureAwait(false);
                return ExitCodes.Failure;
            }

            File.Move(partial, target);
            await output.WriteLineAsync(
                $"Backup written to {target} ({result.Bytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bytes).").ConfigureAwait(false);
            await output.WriteLineAsync(
                "It is encrypted with your passphrase; without the passphrase it is useless, and nothing can recover a lost one. Keep a copy off this machine. "
                + "Restore it, with the service stopped, by running: paduraksa restore <file>").ConfigureAwait(false);
            return ExitCodes.Success;
        }
        catch (SqliteException ex)
        {
            DeleteQuietly(partial);
            await error.WriteLineAsync($"Couldn't read a database: {ex.Message} Nothing was written.").ConfigureAwait(false);
            return ExitCodes.Failure;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(partial);
            await error.WriteLineAsync($"The backup failed: {ex.Message} Nothing was written.").ConfigureAwait(false);
            return ExitCodes.Failure;
        }
    }

    /// <summary>Restores an archive. <see cref="ExitCodes.Failure"/> leaves the data directory as it was.</summary>
    internal static async Task<int> RestoreAsync(PaduraksaHostOptions? hostOptions, string archivePath, PassphraseInput input, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var (options, invalid) = await LoadOptionsAsync(hostOptions, error).ConfigureAwait(false);
        if (options is null)
        {
            return invalid;
        }

        var data = options.DataDirectory;
        if (!File.Exists(archivePath))
        {
            await error.WriteLineAsync($"There is no file {archivePath}.").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        // Before the passphrase is asked for, and held until the end: a server that starts meanwhile finds the directory claimed.
        DataDirectoryLock? claim;
        try
        {
            Directory.CreateDirectory(data);
            claim = DataDirectoryLock.TryAcquire(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"Couldn't use the data directory {data}: {ex.Message}").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        if (claim is null)
        {
            await error.WriteLineAsync($"A Paduraksa server is running on {data}. Stop it first (docker compose stop, or stop the service), then restore. Nothing was changed.").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        using (claim)
        {
            if (!claim.Enforced)
            {
                await error.WriteLineAsync($"Warning: the file system of {data} does not support file locks, so this cannot tell whether a server is running on it. Make sure it is stopped.").ConfigureAwait(false);
            }

            var passphrase = await ReadPassphraseAsync(input, confirm: false, error).ConfigureAwait(false);
            if (passphrase is null)
            {
                return ExitCodes.Failure;
            }

            return await RestoreLockedAsync(options, archivePath, passphrase, output, error, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<int> RestoreLockedAsync(PaduraksaOptions options, string archivePath, string passphrase, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var data = options.DataDirectory;
        var staging = Path.Combine(data, "run", "restore-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)));
        List<(string From, string To)> moved = [];
        List<string> placed = [];
        var masterKeyPath = Path.Combine(data, VaultCommands.GeneratedKeyFileName);
        var hadMasterKey = File.Exists(masterKeyPath);
        string? preRestore = null;
        IStagedBackup? staged = null;
        try
        {
            await using var provider = new ServiceCollection()
                .AddPaduraksaDbContextFactory<ConfigDbContext>(Path.Combine(data, ConfigDatabase.FileName))
                .AddPaduraksaVault(data)
                .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

            // 1. Everything is verified first: header, passphrase, every block's tag, hashes, schemas. Only staging is written.
            await using (var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                staged = await provider.GetRequiredService<IVaultBackup>().OpenAsync(archive, passphrase.AsMemory(), staging, KnownConfigMigrations(), cancellationToken).ConfigureAwait(false);
            }

            await using (staged.ConfigureAwait(false))
            {
                var manifest = staged.Manifest;
                await output.WriteLineAsync(
                    $"Backup made {manifest.CreatedAt:yyyy-MM-dd HH:mm} UTC by Paduraksa {manifest.PaduraksaVersion}; it checks out. Restoring into {data}.").ConfigureAwait(false);

                // 2. The current databases go aside, never away.
                preRestore = Path.Combine(data, "pre-restore-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture));
                foreach (var name in DatabaseFiles)
                {
                    var source = Path.Combine(data, name);
                    if (File.Exists(source))
                    {
                        CreatePrivateDirectory(preRestore);
                        var destination = Path.Combine(preRestore, name);
                        File.Move(source, destination);
                        moved.Add((source, destination));
                    }
                }

                // 3. The restored ones in: the vault re-wrapped under this machine's master key (made if there is none), the rest as they were.
                var vaultPath = Path.Combine(data, "vault.db");
                placed.Add(vaultPath);
                await staged.WriteVaultDatabaseAsync(vaultPath, cancellationToken).ConfigureAwait(false);
                var configPath = Path.Combine(data, ConfigDatabase.FileName);
                placed.Add(configPath);
                File.Move(staged.ConfigDatabasePath, configPath);
                if (staged.LastKnownGoodPath is { } lastGood)
                {
                    var lastGoodPath = Path.Combine(data, "last-good.json");
                    placed.Add(lastGoodPath);
                    File.Move(lastGood, lastGoodPath);
                }

                // 4. What a start checks: the vault opens under the key this machine holds, and the config opens, migrates and has its revisions.
                var status = await provider.GetRequiredService<IVaultLifecycle>().InitializeAsync(cancellationToken).ConfigureAwait(false);
                var pending = await MigrateConfigAsync(configPath, cancellationToken).ConfigureAwait(false);
                var revision = ReadLatestRevision(configPath);

                await output.WriteLineAsync($"Restored. Config: {revision}{(pending > 0 ? $" ({pending.ToString(System.Globalization.CultureInfo.InvariantCulture)} schema update(s) applied)" : string.Empty)}. The vault is under this machine's master key ({Source(status.KekSource)}).").ConfigureAwait(false);
                if (!hadMasterKey && status.KekSource == KekSourceKind.GeneratedFile)
                {
                    await output.WriteLineAsync($"This machine had no master key, so {masterKeyPath} was generated: keep a copy of it apart from the data (see Backups and restore in the docs); the backup does not hold it.").ConfigureAwait(false);
                }

                if (moved.Count > 0)
                {
                    await output.WriteLineAsync($"The databases that were here are kept in {preRestore}. Delete them when you have checked the restore.").ConfigureAwait(false);
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex) when (ex is BackupException or InvalidOperationException or IOException or UnauthorizedAccessException or SqliteException or OperationCanceledException)
        {
            var stuck = Rollback(placed, moved, masterKeyPath, hadMasterKey, data);
            if (ex is OperationCanceledException)
            {
                await error.WriteLineAsync(stuck.Count == 0 ? "Cancelled; the restore was undone." : "Cancelled; " + string.Join(' ', stuck)).ConfigureAwait(false);
                return ExitCodes.Failure;
            }

            await error.WriteLineAsync(
                (ex is BackupException ? ex.Message : $"The restore failed: {ex.Message}") + (stuck.Count == 0 ? " Nothing was changed." : " " + string.Join(' ', stuck))).ConfigureAwait(false);
            return ExitCodes.Failure;
        }
        finally
        {
            DeleteDirectoryQuietly(staging);
            if (preRestore is not null && Directory.Exists(preRestore) && !Directory.EnumerateFileSystemEntries(preRestore).Any())
            {
                DeleteDirectoryQuietly(preRestore);
            }
        }
    }

    private static string Source(KekSourceKind source) => source switch
    {
        KekSourceKind.File => "from PADURAKSA_MASTER_KEY_FILE",
        KekSourceKind.Environment => "from PADURAKSA_MASTER_KEY",
        _ => "master.key in the data directory",
    };

    /// <summary>
    /// Puts the data directory back as it was before the first move: what was placed goes, what was moved comes back, a master key this run
    /// generated goes. What could not be put back is named in the returned sentences (the files are then in the pre-restore directory).
    /// </summary>
    private static List<string> Rollback(List<string> placed, List<(string From, string To)> moved, string masterKeyPath, bool hadMasterKey, string data)
    {
        List<string> stuck = [];
        if (moved.Count == 0 && placed.Count == 0)
        {
            return stuck;
        }

        SqliteConnection.ClearAllPools();
        foreach (var path in placed.Concat([Path.Combine(data, "vault.db-wal"), Path.Combine(data, "vault.db-shm"), Path.Combine(data, ConfigDatabase.FileName + "-wal"), Path.Combine(data, ConfigDatabase.FileName + "-shm")]))
        {
            DeleteQuietly(path);
        }

        foreach (var (from, to) in Enumerable.Reverse(moved))
        {
            try
            {
                File.Move(to, from, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                stuck.Add($"Couldn't put {Path.GetFileName(from)} back ({ex.Message}): it is at {to}; move it to {from}.");
            }
        }

        if (!hadMasterKey)
        {
            DeleteQuietly(masterKeyPath);
        }

        return stuck;
    }

    /// <summary>The config database's migrations in this build, without opening a database.</summary>
    private static IReadOnlyList<string> KnownConfigMigrations()
    {
        using ConfigDbContext context = new(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite("Data Source=:memory:").Options);
        return [.. context.Database.GetMigrations()];
    }

    /// <summary>Opens the restored config database through the normal startup path (which applies an older schema's migrations) and says how many it applied.</summary>
    private static async Task<int> MigrateConfigAsync(string configPath, CancellationToken cancellationToken)
    {
        await using var provider = new ServiceCollection()
            .AddPaduraksaDbContextFactory<ConfigDbContext>(configPath)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var initialized = await ConfigDatabase.InitializeAsync(provider.GetRequiredService<IDbContextFactory<ConfigDbContext>>(), Int64OrderedIdGenerator.Shared, cancellationToken).ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
        return initialized.AppliedMigrations.Count;
    }

    private static string ReadLatestRevision(string configPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = configPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Seq, lower(hex(DocumentHash)) FROM ConfigRevisions ORDER BY Seq DESC LIMIT 1";
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? $"revision #{reader.GetInt64(0).ToString(System.Globalization.CultureInfo.InvariantCulture)} ({reader.GetString(1)[..12]})"
            : "no revisions yet";
    }

    private static async Task<(PaduraksaOptions? Options, int ExitCode)> LoadOptionsAsync(PaduraksaHostOptions? hostOptions, TextWriter error)
    {
        try
        {
            return (PaduraksaHost.LoadOptions(hostOptions), ExitCodes.Success);
        }
        catch (OptionsValidationException ex)
        {
            await error.WriteLineAsync("Invalid configuration:").ConfigureAwait(false);
            foreach (var failure in ex.Failures)
            {
                await error.WriteLineAsync("  " + failure).ConfigureAwait(false);
            }

            return (null, ExitCodes.InvalidConfiguration);
        }
    }

    /// <summary>The passphrase from its first source, or null with the reason on <paramref name="error"/>.</summary>
    private static async Task<string?> ReadPassphraseAsync(PassphraseInput input, bool confirm, TextWriter error)
    {
        var variable = input.GetVariable(PassphraseVariable);
        if (input.File is { Length: > 0 } file)
        {
            if (!string.IsNullOrEmpty(variable))
            {
                await error.WriteLineAsync($"Warning: {PassphraseVariable} is ignored because --passphrase-file is given.").ConfigureAwait(false);
            }

            if (file == "-")
            {
                if (input.StandardInputIsTerminal)
                {
                    await error.WriteLineAsync("Standard input is a terminal: the passphrase would be typed in the clear. Pipe it in, or leave --passphrase-file out to be prompted.").ConfigureAwait(false);
                    return null;
                }

                return await NonEmptyAsync(TrimLineBreak(await input.StandardInput.ReadToEndAsync().ConfigureAwait(false)), error).ConfigureAwait(false);
            }

            try
            {
                if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(file) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) != 0)
                {
                    await error.WriteLineAsync($"Warning: {file} can be read by other users. Restrict it (chmod 600) and delete it once you no longer need it.").ConfigureAwait(false);
                }

                var info = new FileInfo(file);
                if (info.Length > MaxPassphraseBytes)
                {
                    await error.WriteLineAsync($"{file} is too large to be a passphrase.").ConfigureAwait(false);
                    return null;
                }

                return await NonEmptyAsync(TrimLineBreak(await File.ReadAllTextAsync(file, Encoding.UTF8).ConfigureAwait(false)), error).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await error.WriteLineAsync($"Couldn't read {file}: {ex.Message}").ConfigureAwait(false);
                return null;
            }
        }

        if (!string.IsNullOrEmpty(variable))
        {
            return variable;
        }

        if (!input.StandardInputIsTerminal)
        {
            await error.WriteLineAsync($"No passphrase: give --passphrase-file, set {PassphraseVariable}, or run this in a terminal to be prompted.").ConfigureAwait(false);
            return null;
        }

        var first = input.Prompt("Passphrase: ");
        if (confirm)
        {
            var second = first is null ? null : input.Prompt("Repeat it: ");
            if (first != second)
            {
                await error.WriteLineAsync("The two passphrases differ.").ConfigureAwait(false);
                return null;
            }
        }

        return await NonEmptyAsync(first ?? string.Empty, error).ConfigureAwait(false);
    }

    private static string TrimLineBreak(string text) => text.EndsWith("\r\n", StringComparison.Ordinal) ? text[..^2] : text.TrimEnd('\n');

    private static async Task<string?> NonEmptyAsync(string text, TextWriter error)
    {
        if (text.Length > 0)
        {
            return text;
        }

        await error.WriteLineAsync("The passphrase is empty.").ConfigureAwait(false);
        return null;
    }

    /// <summary>Asks for a line on the terminal without echoing it.</summary>
    private static string? PromptHidden(string prompt)
    {
        Console.Error.Write(prompt);
        StringBuilder text = new();
        while (Console.ReadKey(intercept: true) is { Key: not ConsoleKey.Enter } key)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                {
                    text.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }

        Console.Error.WriteLine();
        return text.ToString();
    }

    /// <summary>A new file, mode 0600 from its first byte.</summary>
    private static FileStream CreatePrivateFile(string path)
    {
        FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    /// <summary>The pre-restore directory holds the old databases, so it is owner-only like the data directory's other private directories (SqliteFiles is the vault's, so this is the Server's own copy).</summary>
    internal static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover temporary file of a failed run.
        }
    }

    private static void DeleteDirectoryQuietly(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
