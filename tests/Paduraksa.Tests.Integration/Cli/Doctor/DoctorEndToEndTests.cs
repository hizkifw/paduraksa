using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using Paduraksa.Server.Cli;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Cli.Doctor;

/// <summary><c>paduraksa doctor</c> against a real data directory made by a real server, with the real file system, ports and databases.</summary>
public sealed class DoctorEndToEndTests
{
    private static readonly string[] Statuses = ["ok", "info", "warning", "failure"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A data directory a server has run on (both databases migrated, a master.key beside them) and stopped.</summary>
    private static async Task<string> UsedDataDirectoryAsync()
    {
        var data = TestDataDirectories.NewPath();
        await using var host = TestHost.Create(dataDirectory: data);
        await host.StartAsync(Ct);
        await host.StopAsync(Ct);
        return data;
    }

    /// <summary>Options with real, currently free ports (the doctor tests them by binding), no HTTP/3 and the network left alone.</summary>
    private static PaduraksaHostOptions Options(string data, params (string Key, string Value)[] more)
    {
        Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Paduraksa:Listen:Http"] = TestPorts.FreePort().ToString(CultureInfo.InvariantCulture),
            ["Paduraksa:Listen:Https"] = TestPorts.FreePort().ToString(CultureInfo.InvariantCulture),
            ["Paduraksa:Listen:Admin"] = TestPorts.FreePort().ToString(CultureInfo.InvariantCulture),
        };
        foreach (var (key, value) in more)
        {
            settings[key] = value;
        }

        return TestHost.CreateOptions(settings: settings, dataDirectory: data);
    }

    private static async Task<(int ExitCode, string Output, string Error)> DoctorAsync(PaduraksaHostOptions options, bool json = false)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        var exit = await DoctorCommands.DiagnoseAsync(options, new DoctorCommands.DoctorRequest(Json: json, Offline: true), output, error, Ct);
        return (exit, output.ToString(), error.ToString());
    }

    private static string Snapshot(string data) =>
        string.Join('\n', new DirectoryInfo(data).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
            .OrderBy(f => f.FullName, StringComparer.Ordinal)
            .Select(f => $"{f.FullName} {f.LastWriteTimeUtc.Ticks} {(f is FileInfo file ? file.Length : -1)}"));

    [Fact]
    public async Task AUsedDataDirectoryPassesAndWarnsAboutTheKeyBesideTheVault()
    {
        var data = await UsedDataDirectoryAsync();

        var (exit, output, error) = await DoctorAsync(Options(data));

        Assert.True(exit == 0, output + error);
        Assert.Contains("[ ok ] config-database: paduraksa.db opens read-only and its schema is current", output, StringComparison.Ordinal);
        Assert.Contains("[ ok ] vault-database: vault.db opens read-only and its schema is current", output, StringComparison.Ordinal);
        Assert.Contains($"[ ok ] master-key: The master key comes from {Path.Combine(data, "master.key")} (generated in the data directory) and opens vault.db.", output, StringComparison.Ordinal);
        Assert.Contains("[warn] master-key-location: The master key is stored beside the vault.", output, StringComparison.Ordinal);
        Assert.Contains("[ ok ] listener-http:", output, StringComparison.Ordinal);
        Assert.Contains("Healthy, with warnings", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDoctorChangesNothing()
    {
        var data = await UsedDataDirectoryAsync();
        var before = Snapshot(data);

        await DoctorAsync(Options(data));
        await DoctorAsync(Options(data), json: true);

        Assert.Equal(before, Snapshot(data));
    }

    [Fact]
    public async Task ADirectoryThatDoesNotExistIsAWarningAndCreatesNothing()
    {
        var data = TestDataDirectories.NewPath();

        var (exit, output, _) = await DoctorAsync(Options(data));

        Assert.Equal(0, exit);
        Assert.Contains("[warn] data-directory:", output, StringComparison.Ordinal);
        Assert.Contains("[info] master-key: There is no master key yet", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(data));
    }

    [Fact]
    public async Task ADowngradedConfigDatabaseFailsTheRun()
    {
        var data = await UsedDataDirectoryAsync();
        await RunSqlAsync(Path.Combine(data, "paduraksa.db"), "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('29991231000000_FromTheFuture', '99.0.0')");

        var (exit, output, _) = await DoctorAsync(Options(data));

        Assert.Equal(1, exit);
        Assert.Contains("[FAIL] config-database:", output, StringComparison.Ordinal);
        Assert.Contains("29991231000000_FromTheFuture", output, StringComparison.Ordinal);
        Assert.Contains("downgrade", output, StringComparison.Ordinal);
        Assert.Contains("Not healthy", output, StringComparison.Ordinal);
        Assert.Contains("[ ok ] vault-database:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADowngradedVaultFailsTheRunToo()
    {
        var data = await UsedDataDirectoryAsync();
        await RunSqlAsync(Path.Combine(data, "vault.db"), "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('29991231000000_VaultFuture', '99.0.0')");

        var (exit, output, _) = await DoctorAsync(Options(data));

        Assert.Equal(1, exit);
        Assert.Contains("[FAIL] vault-database:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMigrationBehindThisBinaryIsOnlyInfo()
    {
        var data = await UsedDataDirectoryAsync();
        await RunSqlAsync(Path.Combine(data, "paduraksa.db"), "DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = (SELECT MAX(\"MigrationId\") FROM \"__EFMigrationsHistory\")");

        var (exit, output, _) = await DoctorAsync(Options(data));

        Assert.Equal(0, exit);
        Assert.Contains("[info] config-database: paduraksa.db needs 1 migration", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKeyThatIsNotTheVaultsFailsAndNeverPrintsAnyKey()
    {
        var data = await UsedDataDirectoryAsync();
        var keyFile = Path.Combine(data, "master.key");
        var original = (await File.ReadAllTextAsync(keyFile, Ct)).Trim();
        var other = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await File.WriteAllTextAsync(keyFile, other + "\n", Ct);

        var (exit, output, error) = await DoctorAsync(Options(data));

        Assert.Equal(1, exit);
        Assert.Contains("[FAIL] master-key: The master key", output, StringComparison.Ordinal);
        Assert.Contains("not the one vault.db was written under", output, StringComparison.Ordinal);
        Assert.DoesNotContain(original, output + error, StringComparison.Ordinal);
        Assert.DoesNotContain(other, output + error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKeyFileOthersCanReadFailsBecauseTheVaultRefusesIt()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix file modes.
        }

        var data = await UsedDataDirectoryAsync();
        File.SetUnixFileMode(Path.Combine(data, "master.key"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        var (exit, output, _) = await DoctorAsync(Options(data));

        Assert.Equal(1, exit);
        Assert.Contains("[FAIL] master-key:", output, StringComparison.Ordinal);
        Assert.Contains("accessible to other users", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALooseVaultFileIsAWarning()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix file modes.
        }

        var data = await UsedDataDirectoryAsync();
        File.SetUnixFileMode(Path.Combine(data, "vault.db"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var (exit, output, _) = await DoctorAsync(Options(data));

        Assert.Equal(0, exit);
        Assert.Contains("[warn] file-permissions:", output, StringComparison.Ordinal);
        Assert.Contains("vault.db (mode 640)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APortSomethingElseHoldsFailsTheRun()
    {
        var data = await UsedDataDirectoryAsync();
        using Socket holder = new(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
        holder.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        holder.Listen(1);
        var port = ((IPEndPoint)holder.LocalEndPoint!).Port.ToString(CultureInfo.InvariantCulture);

        var (exit, output, _) = await DoctorAsync(Options(data, ("Paduraksa:Listen:Http", port)));

        Assert.Equal(1, exit);
        Assert.Contains($"[FAIL] listener-http: public HTTP (tcp {port}) is held by another process", output, StringComparison.Ordinal);
        Assert.Contains("[ ok ] listener-https:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunningServerHoldsItsAdminPortAndIsReported()
    {
        var data = TestDataDirectories.NewPath();
        await using var host = TestHost.Create(dataDirectory: data);
        await host.StartAsync(Ct);
        try
        {
            var port = TestHost.Endpoint(host, HostRole.Admin, "https").Port.ToString(CultureInfo.InvariantCulture);

            var (exit, output, _) = await DoctorAsync(Options(data, ("Paduraksa:Listen:Admin", port), ("Paduraksa:Listen:AdminBind", "127.0.0.1")));

            Assert.Equal(0, exit);
            Assert.Contains($"[ ok ] listener-admin: admin (tcp {port}) is held by the running Paduraksa", output, StringComparison.Ordinal);
            Assert.Contains("[ ok ] server: A server is running", output, StringComparison.Ordinal);
            Assert.Contains("[ ok ] config-database:", output, StringComparison.Ordinal);
            Assert.Contains("[ ok ] master-key:", output, StringComparison.Ordinal);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AnInvalidConfigurationFailsWithExitOneAndTheRestOfTheReportStillPrints()
    {
        var data = TestDataDirectories.NewPath();
        var options = TestHost.CreateOptions(allowEphemeralPorts: false, dataDirectory: data);

        var (exit, output, _) = await DoctorAsync(options);

        Assert.Equal(1, exit);
        Assert.Contains("[FAIL] options:", output, StringComparison.Ordinal);
        Assert.Contains("[info] listener", output.Replace("listeners:", "listener:", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("[info] clock:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheJsonReportHasTheDocumentedShape()
    {
        var data = await UsedDataDirectoryAsync();
        await RunSqlAsync(Path.Combine(data, "paduraksa.db"), "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('29991231000000_FromTheFuture', '99.0.0')");

        var (exit, output, _) = await DoctorAsync(Options(data), json: true);

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        Assert.Equal(["version", "passed", "summary", "checks"], root.EnumerateObject().Select(p => p.Name));
        Assert.False(root.GetProperty("passed").GetBoolean());
        Assert.Equal(1, exit);
        var summary = root.GetProperty("summary");
        Assert.Equal(["ok", "info", "warning", "failure"], summary.EnumerateObject().Select(p => p.Name));
        var checks = root.GetProperty("checks").EnumerateArray().ToList();
        Assert.Equal(checks.Count, summary.EnumerateObject().Sum(p => p.Value.GetInt32()));
        foreach (var check in checks)
        {
            var names = check.EnumerateObject().Select(p => p.Name).ToList();
            Assert.Equal(["id", "status", "message"], names.Take(3));
            var status = check.GetProperty("status").GetString();
            Assert.Contains(status, Statuses);
            Assert.Equal(status is "warning" or "failure", check.TryGetProperty("fix", out _));
        }

        var failure = Assert.Single(checks, c => c.GetProperty("status").GetString() == "failure");
        Assert.Equal("config-database", failure.GetProperty("id").GetString());
        Assert.Equal(1, summary.GetProperty("failure").GetInt32());
    }

    [Fact]
    public async Task TheJsonReportOfAHealthyHostPasses()
    {
        var data = await UsedDataDirectoryAsync();

        var (exit, output, _) = await DoctorAsync(Options(data), json: true);

        using var document = JsonDocument.Parse(output);
        Assert.True(document.RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal(0, exit);
    }

    [Fact]
    public void TheOptionsParse()
    {
        var root = PaduraksaCli.BuildRootCommand();

        Assert.Empty(root.Parse(["doctor"]).Errors);
        Assert.Empty(root.Parse(["doctor", "--json", "--offline", "--token-file", "/tmp/t"]).Errors);
        Assert.Empty(root.Parse(["doctor", "--liveness"]).Errors);
        Assert.NotEmpty(root.Parse(["doctor", "--nope"]).Errors);
    }

    private static async Task RunSqlAsync(string database, string sql)
    {
        SqliteConnectionStringBuilder builder = new() { DataSource = database, Pooling = false };
        await using SqliteConnection connection = new(builder.ToString());
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }
}
