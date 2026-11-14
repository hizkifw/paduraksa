using Microsoft.Data.Sqlite;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Vault.Testing;
using Paduraksa.Vault.Crypto;

namespace Paduraksa.Tests.Vault;

/// <summary><see cref="IVaultInspection"/>: <c>paduraksa doctor</c>'s look at the vault, which must never generate a key or touch a file.</summary>
public sealed class VaultInspectionTests : IDisposable
{
    private readonly VaultHarness _vault = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _vault.Dispose();

    private ValueTask<VaultInspection> InspectAsync() => _vault.Get<IVaultInspection>().InspectAsync(Ct);

    private string Snapshot() =>
        string.Join('\n', new DirectoryInfo(_vault.Data.Path).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
            .OrderBy(f => f.FullName, StringComparer.Ordinal)
            .Select(f => $"{f.FullName} {f.LastWriteTimeUtc.Ticks} {(f is FileInfo file ? file.Length : -1)}"));

    private async Task InitializeAsync()
    {
        await _vault.Get<IVaultLifecycle>().InitializeAsync(Ct);
        _vault.Restart();
        SqliteConnection.ClearAllPools();
    }

    private async Task RunSqlAsync(string sql)
    {
        SqliteConnectionStringBuilder builder = new() { DataSource = _vault.Data.File("vault.db"), Pooling = false };
        await using SqliteConnection connection = new(builder.ToString());
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    [Fact]
    public async Task AnEnvironmentKeyAndNoDatabaseYetIsReportedAndCreatesNothing()
    {
        var inspection = await InspectAsync();

        Assert.Equal(KekSourceKind.Environment, inspection.KekSource);
        Assert.Null(inspection.KekFile);
        Assert.False(inspection.KekBesideDatabase);
        Assert.Null(inspection.KekProblem);
        Assert.False(inspection.DatabaseExists);
        Assert.Equal(VaultKeyCheck.NotChecked, inspection.KeyCheck);
        Assert.Empty(Directory.GetFileSystemEntries(_vault.Data.Path));
    }

    [Fact]
    public async Task NoKeyAnywhereIsNotGeneratedAndSaysWhereOneWouldBe()
    {
        _vault.Variables.Clear();

        var inspection = await InspectAsync();

        Assert.Null(inspection.KekSource);
        Assert.Equal(_vault.Data.File("master.key"), inspection.KekFile);
        Assert.Null(inspection.KekProblem);
        Assert.Empty(Directory.GetFileSystemEntries(_vault.Data.Path));
    }

    [Fact]
    public async Task TheKeyThatOpensTheDatabaseMatchesAndTheMigrationsAreCurrent()
    {
        await InitializeAsync();
        var before = Snapshot();

        var inspection = await InspectAsync();

        Assert.Equal(VaultKeyCheck.Matches, inspection.KeyCheck);
        Assert.True(inspection.DatabaseExists);
        Assert.NotEmpty(inspection.AppliedMigrations);
        Assert.Empty(inspection.PendingMigrations);
        Assert.Empty(inspection.UnknownMigrations);
        Assert.Null(inspection.DatabaseProblem);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task AGeneratedKeyIsBesideTheDatabaseAndIsFoundNotRegenerated()
    {
        _vault.Variables.Clear();
        await InitializeAsync();
        var before = Snapshot();

        var inspection = await InspectAsync();

        Assert.Equal((KekSourceKind.GeneratedFile, _vault.Data.File("master.key"), true), (inspection.KekSource, inspection.KekFile, inspection.KekBesideDatabase));
        Assert.Equal(VaultKeyCheck.Matches, inspection.KeyCheck);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task AKeyFileInsideTheDataDirectoryIsBesideAndOneOutsideIsNot()
    {
        await InitializeAsync();
        var key = _vault.Variables[KekProvider.KeyVariable]!;
        using var elsewhere = new TempDirectory();
        var near = _vault.Data.File("near.key");
        var far = elsewhere.File("far.key");
        await File.WriteAllTextAsync(near, key, Ct);
        await File.WriteAllTextAsync(far, key, Ct);
        _vault.Variables.Clear();

        _vault.Variables[KekProvider.KeyFileVariable] = near;
        _vault.Restart();
        var nearInspection = await InspectAsync();
        _vault.Variables[KekProvider.KeyFileVariable] = far;
        _vault.Restart();
        var farInspection = await InspectAsync();

        Assert.Equal((KekSourceKind.File, near, true), (nearInspection.KekSource, nearInspection.KekFile, nearInspection.KekBesideDatabase));
        Assert.Equal((KekSourceKind.File, far, false), (farInspection.KekSource, farInspection.KekFile, farInspection.KekBesideDatabase));
        Assert.Equal(VaultKeyCheck.Matches, farInspection.KeyCheck);
    }

    [Fact]
    public async Task AKeyThatIsNotTheDatabasesDoesNotMatchAndNothingThrows()
    {
        await InitializeAsync();
        using (var other = Kek.Generate())
        {
            _vault.Variables[KekProvider.KeyVariable] = other.Encode();
        }

        _vault.Restart();
        var inspection = await InspectAsync();

        Assert.Equal(VaultKeyCheck.DoesNotMatch, inspection.KeyCheck);
        Assert.Null(inspection.KekProblem);
    }

    [Fact]
    public async Task AMalformedKeyIsAProblemWithoutTheKeyInIt()
    {
        _vault.Variables[KekProvider.KeyVariable] = "not-a-key-SECRET-LOOKING";

        var inspection = await InspectAsync();

        Assert.NotNull(inspection.KekProblem);
        Assert.Contains("not usable", inspection.KekProblem, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-LOOKING", inspection.KekProblem, StringComparison.Ordinal);
        Assert.Equal(KekSourceKind.Environment, inspection.KekSource);
        Assert.Equal(VaultKeyCheck.NotChecked, inspection.KeyCheck);
    }

    [Fact]
    public async Task ALostGeneratedKeyBesideADatabaseWithDataIsAProblemAndIsNotRegenerated()
    {
        _vault.Variables.Clear();
        await InitializeAsync();
        File.Delete(_vault.Data.File("master.key"));

        var inspection = await InspectAsync();

        Assert.Contains("there is no master key", inspection.KekProblem, StringComparison.Ordinal);
        Assert.False(File.Exists(_vault.Data.File("master.key")));
    }

    [Fact]
    public async Task AGeneratedKeyFileOthersCanReadIsTheVaultsOwnRefusal()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix file modes.
        }

        _vault.Variables.Clear();
        await InitializeAsync();
        File.SetUnixFileMode(_vault.Data.File("master.key"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        var inspection = await InspectAsync();

        Assert.Contains("accessible to other users", inspection.KekProblem, StringComparison.Ordinal);
        Assert.Equal(KekSourceKind.GeneratedFile, inspection.KekSource);
        Assert.True(inspection.KekBesideDatabase);
    }

    [Fact]
    public async Task MigrationsTheBuildLacksAreUnknownAndTheKeyIsNotComparedAgainstAShapeItMayNotKnow()
    {
        await InitializeAsync();
        await RunSqlAsync("INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('29991231000000_Future', '99.0.0')");

        var inspection = await InspectAsync();

        Assert.Equal(["29991231000000_Future"], inspection.UnknownMigrations);
        Assert.Equal(VaultKeyCheck.NotChecked, inspection.KeyCheck);
    }

    [Fact]
    public async Task AMigrationTheDatabaseLacksIsPending()
    {
        await InitializeAsync();
        await RunSqlAsync("DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = (SELECT MAX(\"MigrationId\") FROM \"__EFMigrationsHistory\")");

        var inspection = await InspectAsync();

        Assert.Single(inspection.PendingMigrations);
        Assert.Empty(inspection.UnknownMigrations);
    }

    [Fact]
    public async Task AFileThatIsNotADatabaseIsAProblemNotAnException()
    {
        await File.WriteAllTextAsync(_vault.Data.File("vault.db"), "this is not sqlite, and it is longer than a header would be to be sure of it", Ct);

        var inspection = await InspectAsync();

        Assert.True(inspection.DatabaseExists);
        Assert.NotNull(inspection.DatabaseProblem);
    }
}
