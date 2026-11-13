using System.CommandLine;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Data.Sqlite;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Identity;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Contracts.Notifications;
using Paduraksa.Core.Hosting;
using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;
using Paduraksa.Server.Cli;
using Paduraksa.Server.Hosting;
using Paduraksa.Storage.Sqlite;
using Paduraksa.Tests.Integration.Admin;
using Paduraksa.Tests.Integration.Config;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Integration.Config.ConfigTestSupport;

namespace Paduraksa.Tests.Integration.Cli;

/// <summary>
/// <c>paduraksa backup</c> and <c>paduraksa restore</c> over real data directories: what a backup holds, that it is made while the server runs,
/// that it restores on a machine with another master key, and every way it must refuse or undo.
/// </summary>
public sealed class BackupCommandsTests
{
    private const string Passphrase = "correct horse battery staple";

    private static readonly Dictionary<string, string?> NoPlaceholder = new() { ["Paduraksa:Development:PlaceholderCertificate"] = "false" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BackupCommands.PassphraseInput FromFile(string path) => new(path, _ => null, TextReader.Null, StandardInputIsTerminal: false, _ => null);

    private static string PassphraseFile(string passphrase = Passphrase)
    {
        var path = Path.Combine(TestDataDirectories.NewPath() + "-pass");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, passphrase + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return path;
    }

    private static string NewArchivePath()
    {
        var directory = TestDataDirectories.NewPath() + "-backups";
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "x.pdkbak");
    }

    private static async Task<(int Exit, string Output, string Error)> BackupAsync(string data, string archive, BackupCommands.PassphraseInput? input = null)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        var exit = await BackupCommands.BackupAsync(TestHost.CreateOptions(dataDirectory: data), archive, input ?? FromFile(PassphraseFile()), output, error, Ct);
        return (exit, output.ToString(), error.ToString());
    }

    private static async Task<(int Exit, string Output, string Error)> RestoreAsync(string data, string archive, BackupCommands.PassphraseInput? input = null)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        var exit = await BackupCommands.RestoreAsync(TestHost.CreateOptions(dataDirectory: data), archive, input ?? FromFile(PassphraseFile()), output, error, Ct);
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>A data directory the server has run on: one respond host, one self-signed certificate. The host is stopped.</summary>
    private static async Task<(string Data, string Fingerprint)> SourceAsync()
    {
        var data = TestDataDirectories.NewPath();
        string fingerprint;
        await using (var host = TestHost.Create(settings: NoPlaceholder, dataDirectory: data))
        {
            await host.StartAsync(Ct);
            fingerprint = (await host.Shared.CertificateOperations.IssueSelfSignedAsync(new SelfSignedCertificateRequest(["bk.test"]), Origin, Ct)).Certificate!.Sha256Fingerprint;
            await CommitAsync(host, new UpsertHostCommand(RespondHost("r", "bk.test", "from the source")), Ct);
            await host.StopAsync(Ct);
        }

        // Pooled connections outlive the host; the pool closes idle ones on a timer, and the last close folds the WAL into the
        // database file. Close them now, so a test that compares the directory's bytes before and after isn't racing that timer.
        foreach (var file in new[] { "paduraksa.db", "vault.db" })
        {
            using SqliteConnection connection = new(PaduraksaSqlite.ConnectionString(Path.Combine(data, file)));
            SqliteConnection.ClearPool(connection);
        }

        return (data, fingerprint);
    }

    private static string LatestRevisionHash(string data)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(data, "paduraksa.db")};Mode=ReadOnly;Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Seq || ':' || lower(hex(DocumentHash)) FROM ConfigRevisions ORDER BY Seq DESC LIMIT 1";
        return (string)command.ExecuteScalar()!;
    }

    private static List<string> AuditDetails(string data, string action)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(data, "paduraksa.db")};Mode=ReadOnly;Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Outcome || '|' || ifnull(Details, '') FROM AuditEvents WHERE Action = $action ORDER BY Id";
        command.Parameters.AddWithValue("$action", action);
        using var reader = command.ExecuteReader();
        List<string> rows = [];
        while (reader.Read())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    [Fact]
    public async Task ABackupMadeWhileTheServerRunsRestoresOnAMachineWithAnotherMasterKeyAndEverythingStillWorks()
    {
        var sourceData = TestDataDirectories.NewPath();
        var archive = NewArchivePath();
        const string Totp = "alice";
        string sourceHash;
        string fingerprint;
        string uri;
        var notification = NotificationChannelId.New();

        await using (var source = TestHost.Create(settings: NoPlaceholder, dataDirectory: sourceData))
        {
            await source.StartAsync(Ct);
            fingerprint = (await source.Shared.CertificateOperations.IssueSelfSignedAsync(new SelfSignedCertificateRequest(["bk.test"]), Origin, Ct)).Certificate!.Sha256Fingerprint;
            AccessListConfig list = new(TestIds.AccessList("lan"), "office", BasicAuth: new AccessListBasicAuth(Users: [new AccessListUserConfig(Totp, Password: Passphrase)]));
            await CommitAsync(source, new UpsertAccessListCommand(list), Ct);
            await CommitAsync(source, new UpsertHostCommand(RespondHost("r", "bk.test", "from the source") with { Settings = new HostSettings { AccessList = new AccessListSettings { AccessListId = list.Id } } }), Ct);
            var channel = await source.Shared.Notifications.CreateAsync(
                new NotificationChannelBody(NotificationChannelKind.Webhook, "ops", [NotificationEventKind.UpstreamDown], Secret: "https://hooks.example.com/in/very-secret-path"), Origin, Ct);
            Assert.Equal(IssuanceChangeStatus.Committed, channel.Status);
            notification = channel.Channel!.Id;

            var token = (await File.ReadAllTextAsync(Path.Combine(sourceData, SetupService.TokenFileName), Ct)).Trim();
            ClientContext client = new(Channel.Api, IPAddress.Loopback, "test");
            var owner = (await source.Shared.Setup.CompleteSetupAsync(token, "root", AdminApiHarness.Password, null, client, Ct)).Owner!;
            CommandOrigin asOwner = new(Actor.ForUser(owner.Id), Channel.Api, IPAddress.Loopback);
            uri = (await source.Shared.TwoFactor.BeginEnrollmentAsync(asOwner, AdminApiHarness.Password, Ct)).Enrollment!.ProvisioningUri.ToString();
            Assert.Equal(IdentityStatus.Succeeded, (await source.Shared.TwoFactor.ConfirmEnrollmentAsync(asOwner, TotpOracle.CodeFor(uri), Ct)).Status);

            // The server is up and serving while the backup runs.
            var (exit, output, error) = await BackupAsync(sourceData, archive);
            Assert.True(exit == ExitCodes.Success, error);
            Assert.Contains("encrypted", output, StringComparison.Ordinal);
            Assert.DoesNotContain(Passphrase, output + error, StringComparison.Ordinal);
            await source.StopAsync(Ct);
        }

        sourceHash = LatestRevisionHash(sourceData);

        // A machine that has never run Paduraksa: its own master key will be generated.
        var targetData = TestDataDirectories.NewPath();
        var (restoreExit, restoreOutput, restoreError) = await RestoreAsync(targetData, archive);

        Assert.True(restoreExit == ExitCodes.Success, restoreError);
        Assert.Contains("master.key", restoreOutput, StringComparison.Ordinal);
        Assert.NotEqual(await File.ReadAllTextAsync(Path.Combine(sourceData, "master.key"), Ct), await File.ReadAllTextAsync(Path.Combine(targetData, "master.key"), Ct));
        Assert.Equal(sourceHash, LatestRevisionHash(targetData));
        Assert.Empty(Directory.GetFileSystemEntries(targetData, "pre-restore-*"));

        await using var target = TestHost.Create(settings: NoPlaceholder, dataDirectory: targetData);
        await target.StartAsync(Ct);

        // The certificate's private key opens, and is the one the source served.
        Assert.Equal(fingerprint, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData((await VaultCertificatesEndToEndTests.HandshakeAsync(target, "bk.test")).Certificate.RawData)));

        // The access list's password verifier works: the right password gets the page, none gets a challenge.
        using HttpClient client2 = new();
        using var anonymous = Request(target, "http", "bk.test", "/");
        using var challenged = await client2.SendAsync(anonymous, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, challenged.StatusCode);
        using var authorized = Request(target, "http", "bk.test", "/");
        authorized.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Totp}:{Passphrase}")));
        using var served = await client2.SendAsync(authorized, Ct);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("from the source", await served.Content.ReadAsStringAsync(Ct));

        // The notification channel is there (its secret is sealed in the vault; the vault's own tests read every kind back).
        Assert.Contains(notification, (await target.Shared.Notifications.ListAsync(Ct)).Select(c => c.Id));

        // The owner signs in with the password and the authenticator, whose secret was in the vault.
        ClientContext signIn = new(Channel.Api, IPAddress.Loopback, "test");
        var login = await target.Shared.Login.LoginAsync("root", AdminApiHarness.Password, signIn, Ct);
        Assert.Equal(LoginStatus.SecondFactorRequired, login.Status);
        var second = await target.Shared.Login.CompleteSecondFactorAsync(login.Session!.CookieValue, TotpOracle.CodeFor(uri, stepOffset: 1), null, signIn, Ct);
        Assert.Equal(LoginStatus.Succeeded, second.Status);
    }

    [Fact]
    public async Task TheBackupIsAuditedUnderTheCliActorWithItsSizeAndNeverThePassphrase()
    {
        var (data, _) = await SourceAsync();
        var archive = NewArchivePath();

        var (exit, output, error) = await BackupAsync(data, archive);

        Assert.True(exit == ExitCodes.Success, error);
        var audit = Assert.Single(AuditDetails(data, "backup.create"));
        Assert.Equal($"success|{{\"bytes\":{new FileInfo(archive).Length}}}", audit);
        Assert.DoesNotContain(Passphrase, audit + output + error, StringComparison.Ordinal);
        Assert.DoesNotContain(Passphrase, Encoding.Latin1.GetString(await File.ReadAllBytesAsync(archive, Ct)), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePassphraseCanNotBeGivenOnTheCommandLine()
    {
        var root = PaduraksaCli.BuildRootCommand();

        foreach (var argv in new[] { "backup --out x --passphrase hunter2hunter2", "backup --out x --passphrase-file f --password hunter2", "restore x --passphrase hunter2hunter2" })
        {
            Assert.NotEmpty(root.Parse(argv).Errors);
        }

        var backup = root.Subcommands.Single(c => c.Name == "backup");
        Assert.DoesNotContain(backup.Options, o => o.Name.Contains("passphrase", StringComparison.Ordinal) && o.Name != "--passphrase-file");
        Assert.Empty(root.Parse("backup --out x --passphrase-file f").Errors);
    }

    [Fact]
    public async Task ABackupNeverOverwritesAndNeedsADatabaseToBackUp()
    {
        var (data, _) = await SourceAsync();
        var archive = NewArchivePath();
        await File.WriteAllTextAsync(archive, "precious", Ct);

        var (exit, _, error) = await BackupAsync(data, archive);

        Assert.Equal(ExitCodes.Failure, exit);
        Assert.Contains("never overwrites", error, StringComparison.Ordinal);
        Assert.Equal("precious", await File.ReadAllTextAsync(archive, Ct));

        var (emptyExit, _, emptyError) = await BackupAsync(TestDataDirectories.NewPath(), NewArchivePath());
        Assert.Equal(ExitCodes.Failure, emptyExit);
        Assert.Contains("no config database", emptyError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWeakPassphraseIsRefusedAndNothingIsWritten()
    {
        var (data, _) = await SourceAsync();
        var archive = NewArchivePath();

        var (exit, _, error) = await BackupAsync(data, archive, FromFile(PassphraseFile("short")));

        Assert.Equal(ExitCodes.Failure, exit);
        Assert.Contains("at least 12", error, StringComparison.Ordinal);
        Assert.False(File.Exists(archive));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(archive)!, "*partial*"));
    }

    [Fact]
    public async Task ThePassphraseComesFromAFileTheVariableStandardInputOrAPromptInThatOrder()
    {
        var (data, _) = await SourceAsync();
        var prompts = 0;
        string? Prompt(string _)
        {
            prompts++;
            return Passphrase;
        }

        // The variable alone.
        Assert.Equal(ExitCodes.Success, (await BackupAsync(data, NewArchivePath(), new(null, name => name == BackupCommands.PassphraseVariable ? Passphrase : null, TextReader.Null, false, Prompt))).Exit);
        // Standard input, when a file named - says so.
        Assert.Equal(ExitCodes.Success, (await BackupAsync(data, NewArchivePath(), new("-", _ => null, new StringReader(Passphrase + "\n"), false, Prompt))).Exit);
        // A terminal: asked twice, and the two must agree.
        Assert.Equal(ExitCodes.Success, (await BackupAsync(data, NewArchivePath(), new(null, _ => null, TextReader.Null, true, Prompt))).Exit);
        Assert.Equal(2, prompts);
        var answers = new Queue<string>(["a long enough phrase", "a different phrase"]);
        var mismatch = await BackupAsync(data, NewArchivePath(), new(null, _ => null, TextReader.Null, true, _ => answers.Dequeue()));
        Assert.Equal(ExitCodes.Failure, mismatch.Exit);
        Assert.Contains("differ", mismatch.Error, StringComparison.Ordinal);
        // A file wins over the variable, and says so.
        var both = await BackupAsync(data, NewArchivePath(), new(PassphraseFile(), name => name == BackupCommands.PassphraseVariable ? "something else entirely" : null, TextReader.Null, false, Prompt));
        Assert.Equal(ExitCodes.Success, both.Exit);
        Assert.Contains("ignored", both.Error, StringComparison.Ordinal);
        // Nothing to ask and nothing given.
        var none = await BackupAsync(data, NewArchivePath(), new(null, _ => null, TextReader.Null, false, Prompt));
        Assert.Equal(ExitCodes.Failure, none.Exit);
        Assert.Contains("No passphrase", none.Error, StringComparison.Ordinal);
        // Standard input that is a terminal is never read as a file.
        var terminal = await BackupAsync(data, NewArchivePath(), new("-", _ => null, new StringReader(Passphrase), true, Prompt));
        Assert.Equal(ExitCodes.Failure, terminal.Exit);
        Assert.Contains("terminal", terminal.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreRefusesWhileAServerRunsOnTheDataDirectoryAndChangesNothing()
    {
        var (source, _) = await SourceAsync();
        var archive = NewArchivePath();
        Assert.Equal(ExitCodes.Success, (await BackupAsync(source, archive)).Exit);
        var data = TestDataDirectories.NewPath();
        await using var running = TestHost.Create(settings: NoPlaceholder, dataDirectory: data);
        await running.StartAsync(Ct);
        var before = LatestRevisionHashOrNone(data);

        var (exit, _, error) = await RestoreAsync(data, archive);

        Assert.Equal(ExitCodes.Failure, exit);
        Assert.Contains("server is running", error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(data, "pre-restore-*"));
        Assert.Equal(before, LatestRevisionHashOrNone(data));
        await running.StopAsync(Ct);
        await running.DisposeAsync(); // The claim is released with the last shared service, as when the process ends.

        // Stopped, it is the same restore that works.
        Assert.Equal(ExitCodes.Success, (await RestoreAsync(data, archive)).Exit);
    }

    [Fact]
    public async Task ASecondServerOnTheSameDataDirectoryFailsToStart()
    {
        var data = TestDataDirectories.NewPath();
        await using var first = TestHost.Create(dataDirectory: data);
        await first.StartAsync(Ct);

        var ex = Assert.Throws<DataDirectoryInUseException>(() => TestHost.Create(dataDirectory: data));

        Assert.Contains("in use by another Paduraksa process", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreOverAnInstallationKeepsTheOldDatabasesAsideAndItsMasterKey()
    {
        var (data, _) = await SourceAsync();
        var archive = NewArchivePath();
        Assert.Equal(ExitCodes.Success, (await BackupAsync(data, archive)).Exit);
        var backedUpHash = LatestRevisionHash(data);
        var masterKey = await File.ReadAllTextAsync(Path.Combine(data, "master.key"), Ct);

        // The installation moves on after the backup.
        await using (var later = TestHost.Create(settings: NoPlaceholder, dataDirectory: data))
        {
            await later.StartAsync(Ct);
            await CommitAsync(later, new UpsertHostCommand(RespondHost("later", "later.test", "added after the backup")), Ct);
            await later.StopAsync(Ct);
        }

        var movedOnHash = LatestRevisionHash(data);
        Assert.NotEqual(backedUpHash, movedOnHash);

        var (exit, output, error) = await RestoreAsync(data, archive);

        Assert.True(exit == ExitCodes.Success, error);
        Assert.Equal(backedUpHash, LatestRevisionHash(data));
        Assert.Equal(masterKey, await File.ReadAllTextAsync(Path.Combine(data, "master.key"), Ct));
        var aside = Assert.Single(Directory.GetDirectories(data, "pre-restore-*"));
        Assert.Equal(movedOnHash, LatestRevisionHash(aside));
        Assert.True(File.Exists(Path.Combine(aside, "vault.db")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(aside));
        }

        Assert.Contains(Path.GetFileName(aside), output, StringComparison.Ordinal);
        Assert.DoesNotContain("generated", output, StringComparison.Ordinal);
        Assert.Empty(Directory.GetDirectories(Path.Combine(data, "run"), "restore-*"));

        // And it starts.
        await using var host = TestHost.Create(settings: NoPlaceholder, dataDirectory: data);
        await host.StartAsync(Ct);
    }

    [Fact]
    public async Task AWrongPassphraseATamperedByteAndATruncatedFileAreRefusedBeforeAnythingIsTouched()
    {
        var (source, _) = await SourceAsync();
        var archive = NewArchivePath();
        Assert.Equal(ExitCodes.Success, (await BackupAsync(source, archive)).Exit);
        var bytes = await File.ReadAllBytesAsync(archive, Ct);
        var data = TestDataDirectories.NewPath();
        await using (var existing = TestHost.Create(settings: NoPlaceholder, dataDirectory: data))
        {
            await existing.StartAsync(Ct);
            await existing.StopAsync(Ct);
        }

        var before = Snapshot(data);

        var wrong = await RestoreAsync(data, archive, FromFile(PassphraseFile("not the passphrase at all")));
        Assert.Equal(ExitCodes.Failure, wrong.Exit);
        Assert.Contains("passphrase is wrong", wrong.Error, StringComparison.Ordinal);

        var tampered = bytes.ToArray();
        tampered[tampered.Length / 2] ^= 0x01;
        var tamperedPath = NewArchivePath();
        await File.WriteAllBytesAsync(tamperedPath, tampered, Ct);
        var changed = await RestoreAsync(data, tamperedPath);
        Assert.Equal(ExitCodes.Failure, changed.Exit);
        Assert.Contains("damaged", changed.Error, StringComparison.Ordinal);

        var cutPath = NewArchivePath();
        await File.WriteAllBytesAsync(cutPath, bytes[..(bytes.Length / 2)], Ct);
        var cut = await RestoreAsync(data, cutPath);
        Assert.Equal(ExitCodes.Failure, cut.Exit);
        Assert.Contains("damaged", cut.Error, StringComparison.Ordinal);

        var notABackup = NewArchivePath();
        await File.WriteAllTextAsync(notABackup, "this is just some text that is longer than a header", Ct);
        Assert.Contains("not a Paduraksa backup", (await RestoreAsync(data, notABackup)).Error, StringComparison.Ordinal);

        Assert.Equal(before, Snapshot(data));
        Assert.Empty(Directory.GetFileSystemEntries(data, "pre-restore-*"));
    }

    [Fact]
    public async Task AnArchiveFromANewerSchemaIsRefusedAndNothingIsChanged()
    {
        var (source, _) = await SourceAsync();
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(source, "paduraksa.db")};Pooling=false"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('29990101000000_FromTheFuture', '99.0.0')";
            command.ExecuteNonQuery();
        }

        var archive = NewArchivePath();
        Assert.Equal(ExitCodes.Success, (await BackupAsync(source, archive)).Exit);
        var data = TestDataDirectories.NewPath();

        var (exit, _, error) = await RestoreAsync(data, archive);

        Assert.Equal(ExitCodes.Failure, exit);
        Assert.Contains("29990101000000_FromTheFuture", error, StringComparison.Ordinal);
        Assert.Contains("newer", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(data, "paduraksa.db")));
        Assert.False(File.Exists(Path.Combine(data, "master.key")));
    }

    [Fact]
    public async Task AFailureAfterTheDatabasesWereMovedAsideUndoesEverything()
    {
        var (source, _) = await SourceAsync();
        var archive = NewArchivePath();
        Assert.Equal(ExitCodes.Success, (await BackupAsync(source, archive)).Exit);
        var (data, _) = await SourceAsync();
        // Something else is where the last-known-good file has to go: the last step of the install fails, after both databases were replaced.
        File.Delete(Path.Combine(data, "last-good.json"));
        Directory.CreateDirectory(Path.Combine(data, "last-good.json"));
        var before = Snapshot(data);

        var (exit, _, error) = await RestoreAsync(data, archive);

        Assert.Equal(ExitCodes.Failure, exit);
        Assert.Contains("failed", error, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", error, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(data));
    }

    /// <summary>The names, sizes and hashes of the files of a data directory (not its run directory), to compare before and after.</summary>
    private static List<string> Snapshot(string data) =>
    [
        .. Directory.EnumerateFiles(data).Select(Path.GetFileName).Order(StringComparer.Ordinal)
            .Select(name => $"{name}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(data, name!))))}"),
        .. Directory.EnumerateDirectories(data).Select(Path.GetFileName).Where(n => n != "run").Order(StringComparer.Ordinal).Select(n => "dir:" + n),
    ];

    private static string LatestRevisionHashOrNone(string data)
    {
        try
        {
            return LatestRevisionHash(data);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidCastException or NullReferenceException)
        {
            return "none";
        }
    }
}
