using System.Runtime.Versioning;

using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Ids;
using Paduraksa.Data;
using Paduraksa.Storage.Sqlite;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Config;

/// <summary>
/// The config database holds password hashes, session and token hashes and the audit log, so it and its <c>-wal</c> and <c>-shm</c> are readable by
/// their owner alone (threat model G7), however the process umask or the data directory is set.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class ConfigDatabaseFileModeTests
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly string[] RootOwnedFiles = ["/etc/passwd", "/etc/hosts", "/etc/hostname"];

    private static Int64OrderedIdGenerator Generator => new(TimeProvider.System);

    [Fact]
    public async Task AFreshDatabaseAndItsWalAndSharedMemoryAreCreatedForTheirOwnerAlone()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes");
        await using var db = Empty();
        await ConfigDatabase.InitializeAsync(db.Factory, Generator, Ct);

        // A held connection with a write keeps the WAL and the shared memory on disk.
        await using var context = db.NewContext();
        await context.Database.OpenConnectionAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("CREATE TABLE ModeProbe (X INTEGER)", Ct);

        foreach (var path in new[] { db.DatabasePath, db.DatabasePath + "-wal", db.DatabasePath + "-shm" })
        {
            Assert.True(File.Exists(path), $"{Path.GetFileName(path)} should exist while a connection is open");
            Assert.Equal(OwnerOnly, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public async Task AnExistingDatabaseThatOthersCouldReadIsRestrictedAtStartup_AndTheResultSaysWhichFiles()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes");
        await using var db = await CreateAsync();
        await using var context = db.NewContext();
        await context.Database.OpenConnectionAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("CREATE TABLE ModeProbe (X INTEGER)", Ct);
        var loose = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        var files = new[] { db.DatabasePath, db.DatabasePath + "-wal", db.DatabasePath + "-shm" }.Where(File.Exists).ToList();
        Assert.Contains(db.DatabasePath, files);
        foreach (var path in files)
        {
            File.SetUnixFileMode(path, loose);
        }

        var result = await ConfigDatabase.InitializeAsync(db.Factory, Generator, Ct);

        Assert.Equal(files, result.RestrictedFiles);
        Assert.All(files, path => Assert.Equal(OwnerOnly, File.GetUnixFileMode(path)));
    }

    [Fact]
    public async Task ADatabaseThatIsAlreadyPrivateIsLeftAloneAndNothingIsReported()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes");
        await using var db = await CreateAsync();

        var result = await ConfigDatabase.InitializeAsync(db.Factory, Generator, Ct);

        Assert.Empty(result.RestrictedFiles);
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(db.DatabasePath));
    }

    [Fact]
    public void AFileTheProcessCannotChangeIsReportedWithItsModeAndTheReason_NotLeftWorldReadableInSilence()
    {
        // A file that belongs to someone else and that others can read, which chmod refuses: a stand-in for a database owned by another uid at 0660.
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "root can change any file, and this test must not");
        var other = RootOwnedFiles.FirstOrDefault(path => File.Exists(path) && (File.GetUnixFileMode(path) & UnixFileMode.OtherRead) != 0);
        Assert.SkipWhen(other is null, "no root-owned, world-readable file to stand in");

        var result = PrivateSqliteFile.Restrict(other!);

        Assert.Empty(result.Changed);
        var failure = Assert.Single(result.Failed);
        Assert.Equal(other, failure.Path);
        Assert.NotEqual(default, failure.Mode & UnixFileMode.OtherRead);
        Assert.False(string.IsNullOrWhiteSpace(failure.Reason));
    }
}
