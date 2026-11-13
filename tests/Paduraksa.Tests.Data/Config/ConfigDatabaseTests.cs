using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Paduraksa.Core.Ids;
using Paduraksa.Data;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Config;

public sealed class ConfigDatabaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheModelHasNoChangesMissingAMigration()
    {
        // Fails when an entity or mapping changed without `dotnet ef migrations add` (docs/development.md, "Database migrations").
        await using var db = Empty();
        await using var context = db.NewContext();

        Assert.False(context.Database.HasPendingModelChanges(), "The model changed since the last migration: add one.");
    }

    [Fact]
    public async Task InitializeMigratesAFreshDatabaseFile()
    {
        await using var db = Empty();
        Assert.False(File.Exists(db.DatabasePath));

        var result = await ConfigDatabase.InitializeAsync(db.Factory, new Int64OrderedIdGenerator(TimeProvider.System), Ct);

        await using var context = db.NewContext();
        Assert.Equal(context.Database.GetMigrations(), result.AppliedMigrations);
        Assert.Contains(result.AppliedMigrations, m => m.EndsWith("_Initial", StringComparison.Ordinal));
        Assert.Contains(result.AppliedMigrations, m => m.EndsWith("_Certificates", StringComparison.Ordinal));
        Assert.Contains(result.AppliedMigrations, m => m.EndsWith("_AcmeIssuance", StringComparison.Ordinal));
        Assert.Contains(result.AppliedMigrations, m => m.EndsWith("_Identity", StringComparison.Ordinal));
        Assert.Empty(result.ObservedKeys);
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
        var tables = await db.QueryAsync("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name");
        Assert.Equal(
            ["AcmeAccounts", "AcmeOrders", "AnalyticsRollups", "ApiTokens", "AuditEvents", "CertificateNames", "Certificates", "ConfigDraftNotices", "ConfigDrafts", "ConfigRevisions", "DnsProviders", "ExpiryNotices", "MetricRollups", "NotificationChannels", "NotificationDeliveries", "NotificationEvents", "RecoveryCodes", "RoleAssignments", "Sessions", "Users", "__EFMigrationsHistory", "__EFMigrationsLock"],
            tables.Select(r => (string)r[0]!).Order(StringComparer.Ordinal));
        Assert.Equal("wal", await db.ScalarAsync("PRAGMA journal_mode"));
    }

    [Fact]
    public async Task InitializeMigratesAZeroByteDatabaseFile()
    {
        // What a crash before SQLite's first write, or a `touch`, leaves behind: an empty database to SQLite.
        await using var db = Empty();
        Directory.CreateDirectory(Path.GetDirectoryName(db.DatabasePath)!);
        await File.WriteAllBytesAsync(db.DatabasePath, [], Ct);

        var result = await ConfigDatabase.InitializeAsync(db.Factory, new Int64OrderedIdGenerator(TimeProvider.System), Ct);

        await using var context = db.NewContext();
        Assert.Equal(context.Database.GetMigrations(), result.AppliedMigrations);
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
        Assert.Equal("wal", await db.ScalarAsync("PRAGMA journal_mode"));
    }

    [Fact]
    public async Task InitializeOnACurrentDatabaseAppliesNothing()
    {
        await using var db = await CreateAsync();

        var result = await ConfigDatabase.InitializeAsync(db.Factory, new Int64OrderedIdGenerator(TimeProvider.System), Ct);

        Assert.Empty(result.AppliedMigrations);
    }

    [Fact]
    public async Task InitializeSeedsTheGeneratorFromTheAuditTable()
    {
        await using var db = await CreateAsync();
        // An event written by a previous run whose clock was a day ahead.
        var persisted = new Int64OrderedIdGenerator(new ManualTimeProvider(Now.AddDays(1))).Next();
        await db.ExecuteAsync(
            "INSERT INTO AuditEvents (Id, Timestamp, ActorKind, Channel, Action, Outcome, PrevHash, Hash) "
            + $"VALUES ({persisted}, '2026-11-08T12:00:00.0000000Z', 'system', 'system', 'test.event', 'success', zeroblob(32), randomblob(32))");
        var generator = new Int64OrderedIdGenerator(new ManualTimeProvider(Now));

        var result = await ConfigDatabase.InitializeAsync(db.Factory, generator, Ct);

        Assert.Equal(new Dictionary<string, long> { ["AuditEvents"] = persisted }, result.ObservedKeys);
        Assert.True(generator.Next() > persisted);
    }

    [Fact]
    public async Task ADatabaseFromBeforeTheProjectionWasDroppedMigratesAndKeepsItsRevisions()
    {
        await using var db = Empty();
        await using (var context = db.NewContext())
        {
            await context.GetService<IMigrator>().MigrateAsync("20261108022504_Identity", Ct);
        }

        // What a server running that schema left behind: a revision and the projection rows that mirror it.
        await db.ExecuteAsync(
            "INSERT INTO ConfigRevisions (Id, Seq, ParentId, ActorKind, Channel, CreatedAt, Document, DocumentHash, Summary) "
            + "VALUES (x'01', 1, NULL, 'system', 'system', '2026-11-08T12:00:00.0000000Z', x'7b7d', zeroblob(32), '[]'); "
            + "INSERT INTO GlobalSettings (Key, RevisionId, Version, UnknownHost, TrustedProxyMode, Defaults) VALUES (1, x'01', 1, 'notFound', 'none', '{}'); "
            + "INSERT INTO Profiles (Id, Name, Settings, Version) VALUES (x'02', 'LAN', '{}', 1)");

        var result = await ConfigDatabase.InitializeAsync(db.Factory, new Int64OrderedIdGenerator(TimeProvider.System), Ct);

        Assert.Equal(["20261111050534_DropConfigProjection", "20261111195820_MetricRollups", "20261112013131_NotificationChannels", "20261112024452_AuditSignInIndex", "20261112055123_MetricRollupCoverage", "20261112125909_AnalyticsRollups", "20261112141908_NotificationOutbox", "20261112200011_ConfigDrafts"], result.AppliedMigrations);
        var tables = (await db.QueryAsync("SELECT name FROM sqlite_master WHERE type = 'table'")).Select(r => (string)r[0]!).ToList();
        Assert.All(["GlobalSettings", "Profiles", "Upstreams", "Destinations", "Hosts", "HostNames", "Routes"], dropped => Assert.DoesNotContain(dropped, tables));
        Assert.Equal(1L, await db.ScalarAsync("SELECT count(*) FROM ConfigRevisions WHERE Seq = 1"));
        await using var upgraded = db.NewContext();
        Assert.Empty(await upgraded.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task AMinuteStoredBeforeCoverageWasRecordedCountsAsAWholeObservedMinute()
    {
        await using var db = Empty();
        await using (var context = db.NewContext())
        {
            await context.GetService<IMigrator>().MigrateAsync("20261112024452_AuditSignInIndex", Ct);
        }

        await db.ExecuteAsync(
            "INSERT INTO MetricRollups (Id, Subject, SubjectKey, Minute, Requests, Status1xx, Status2xx, Status3xx, Status4xx, Status5xx, NoResponse, Refused, Errors, BytesOut, TotalMicrosSum, UpstreamMicrosSum, UpstreamTimed, TotalP50, TotalP95, TotalP99, UpstreamP50, UpstreamP95, UpstreamP99) "
            + "VALUES (1, 0, '', 29000000, 5, 0, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)");

        await ConfigDatabase.InitializeAsync(db.Factory, new Int64OrderedIdGenerator(TimeProvider.System), Ct);

        Assert.Equal(60L, await db.ScalarAsync("SELECT CoveredSeconds FROM MetricRollups"));
    }
}
