using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Ids;
using Paduraksa.Data;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Config;

/// <summary>Migrations and the startup helper, on a real database file.</summary>
public sealed class ConfigDatabaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheModelHasNoChangesMissingAMigration()
    {
        // Fails when an entity or mapping changed without `dotnet ef migrations add` (README, "Database migrations").
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
        Assert.Empty(result.ObservedKeys);
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
        var tables = await db.QueryAsync("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name");
        Assert.Equal(["AuditEvents", "ConfigRevisions", "__EFMigrationsHistory", "__EFMigrationsLock"], tables.Select(r => (string)r[0]!).Order(StringComparer.Ordinal));
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
}
