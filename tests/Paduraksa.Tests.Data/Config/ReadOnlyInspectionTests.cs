using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Paduraksa.Application.Settings;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Data;
using Paduraksa.Storage.Sqlite;
using Paduraksa.Tests.Data.Commands;

namespace Paduraksa.Tests.Data.Config;

/// <summary>The read-only looks the CLI takes at the config database (<c>paduraksa doctor</c>, the sign-in policy): safe beside a server, and they change nothing.</summary>
public sealed class ReadOnlyInspectionTests
{
    private static CancellationToken Ct => TestConfigDatabase.Ct;

    private static string[] Files(string databasePath) =>
        [.. Directory.GetFileSystemEntries(Path.GetDirectoryName(databasePath)!).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

    private static async Task RunSqlAsync(string databasePath, string sql)
    {
        SqliteConnectionStringBuilder builder = new() { DataSource = databasePath, Pooling = false };
        await using SqliteConnection connection = new(builder.ToString());
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>The database with every pooled connection closed, which removes its <c>-wal</c> and <c>-shm</c> files: a server that has stopped.</summary>
    private static async Task<TestConfigDatabase> StoppedAsync()
    {
        var database = await TestConfigDatabase.CreateAsync();
        using (var connection = new SqliteConnection(PaduraksaSqlite.ConnectionString(database.DatabasePath)))
        {
            SqliteConnection.ClearPool(connection);
        }

        return database;
    }

    [Fact]
    public async Task ADatabaseNothingHasOpenIsReadWithoutCreatingAFile()
    {
        await using var database = await StoppedAsync();
        var before = Files(database.DatabasePath);
        Assert.Equal([ConfigDatabase.FileName], before);

        var inspection = await ConfigDatabase.InspectAsync(database.DatabasePath, Ct);

        Assert.True(inspection.Exists);
        Assert.Null(inspection.Problem);
        Assert.True(inspection.Migrations.IsCurrent);
        Assert.NotEmpty(inspection.Migrations.Applied);
        Assert.Equal(before, Files(database.DatabasePath));
    }

    [Fact]
    public async Task ADatabaseAServerHasOpenIsReadAsItIs()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        await using var writer = database.NewContext();
        await writer.Database.GetDbConnection().OpenAsync(Ct);
        Assert.Contains(ConfigDatabase.FileName + "-wal", Files(database.DatabasePath));

        var inspection = await ConfigDatabase.InspectAsync(database.DatabasePath, Ct);

        Assert.True(inspection.Migrations.IsCurrent);
    }

    [Fact]
    public async Task MigrationsTheBuildLacksAreUnknownAndOnesTheDatabaseLacksArePending()
    {
        await using var database = await StoppedAsync();
        await RunSqlAsync(database.DatabasePath, "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('29991231000000_Future', '99.0.0')");
        await RunSqlAsync(database.DatabasePath, "DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = (SELECT MIN(\"MigrationId\") FROM \"__EFMigrationsHistory\")");

        var inspection = await ConfigDatabase.InspectAsync(database.DatabasePath, Ct);

        Assert.Equal(["29991231000000_Future"], inspection.Migrations.Unknown);
        Assert.Single(inspection.Migrations.Pending);
        Assert.False(inspection.Migrations.IsCurrent);
    }

    [Fact]
    public async Task AMissingFileIsReportedAsMissingAndNotCreated()
    {
        await using var database = TestConfigDatabase.Empty();
        Directory.CreateDirectory(Path.GetDirectoryName(database.DatabasePath)!);

        var inspection = await ConfigDatabase.InspectAsync(database.DatabasePath, Ct);

        Assert.False(inspection.Exists);
        Assert.Null(inspection.Problem);
        Assert.Empty(Files(database.DatabasePath));
    }

    [Fact]
    public async Task AFileThatIsNotADatabaseIsAProblemNotAnException()
    {
        await using var database = TestConfigDatabase.Empty();
        Directory.CreateDirectory(Path.GetDirectoryName(database.DatabasePath)!);
        await File.WriteAllTextAsync(database.DatabasePath, "this is not an sqlite database, and long enough that nothing mistakes it for one", Ct);

        var inspection = await ConfigDatabase.InspectAsync(database.DatabasePath, Ct);

        Assert.True(inspection.Exists);
        Assert.NotNull(inspection.Problem);
    }

    [Fact]
    public async Task TheStoredServerSettingsAreTheLatestRevisionsAndNothingIsWritten()
    {
        await using var harness = await ConfigServiceHarness.CreateAsync();
        Assert.Null(await StoredServerSettings.ReadAsync(harness.Db.DatabasePath, Ct));
        var document = (await harness.CurrentAsync()) with { Settings = new GlobalSettings(Server: new ServerSettings { AdminMinPasswordLength = 20, MetricsRetention = TimeSpan.FromDays(7) }) };
        var committed = await harness.RunAsync(new ReplaceConfigCommand(document, BaseRevision: null));
        Assert.Equal(ConfigCommandStatus.Committed, committed.Status);

        var stored = await StoredServerSettings.ReadAsync(harness.Db.DatabasePath, Ct);

        Assert.NotNull(stored);
        Assert.Equal(20, stored.AdminMinPasswordLength);
        Assert.Equal(TimeSpan.FromDays(7), stored.MetricsRetention);
        Assert.Null(stored.AdminTotpIssuer);
    }

    [Fact]
    public async Task NoStoredSettingsWhenTheFileIsMissingOrUnreadable()
    {
        await using var database = TestConfigDatabase.Empty();
        Directory.CreateDirectory(Path.GetDirectoryName(database.DatabasePath)!);
        Assert.Null(await StoredServerSettings.ReadAsync(database.DatabasePath, Ct));

        await File.WriteAllTextAsync(database.DatabasePath, "this is not an sqlite database, and long enough that nothing mistakes it for one", Ct);

        Assert.Null(await StoredServerSettings.ReadAsync(database.DatabasePath, Ct));
    }
}
