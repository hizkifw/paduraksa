using System.Runtime.Versioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Storage.Sqlite;
using Paduraksa.Tests.Vault.Testing;
using Paduraksa.Vault.Crypto;
using Paduraksa.Vault.Persistence;

namespace Paduraksa.Tests.Vault.Persistence;

/// <summary>The vault database: schema, migrations, startup and the KEK check (PLAN §5, §8.3).</summary>
public sealed class VaultDatabaseTests : IDisposable
{
    private readonly TempDirectory _data = new();

    private readonly Kek _kek = Kek.Generate();

    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        _kek.Dispose();
        SqliteConnectionCleanup();
        _data.Dispose();
    }

    private string Path => _data.File(VaultDatabase.FileName);

    private IDbContextFactory<VaultDbContext> Factory()
    {
        var provider = new ServiceCollection().AddVaultDbContextFactory(Path).BuildServiceProvider();
        return provider.GetRequiredService<IDbContextFactory<VaultDbContext>>();
    }

    private Task<VaultDatabaseInitialized> Initialize(Kek? kek = null) =>
        VaultDatabase.InitializeAsync(Factory(), Path, kek ?? _kek, _time);

    /// <summary>Pooled connections keep the file open (and, on Windows, undeletable).</summary>
    private static void SqliteConnectionCleanup() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    [Fact]
    public async Task TheModelHasNoChangesMissingAMigration()
    {
        // Fails when an entity or mapping changed without `dotnet ef migrations add` (README, "Database migrations").
        await using var context = await Factory().CreateDbContextAsync(TestContext.Current.CancellationToken);

        Assert.False(context.Database.HasPendingModelChanges(), "The model changed since the last migration: add one.");
    }

    [Fact]
    public async Task InitializeCreatesAndMigratesTheFileAndStoresTheKekCheck()
    {
        Assert.False(File.Exists(Path));

        var first = await Initialize();
        var second = await Initialize();

        Assert.Equal(4, first.AppliedMigrations.Count);
        Assert.EndsWith("_Initial", first.AppliedMigrations[0], StringComparison.Ordinal);
        Assert.EndsWith("_Certificates", first.AppliedMigrations[1], StringComparison.Ordinal);
        Assert.EndsWith("_Totp", first.AppliedMigrations[2], StringComparison.Ordinal);
        Assert.EndsWith("_DataProtectionKeys", first.AppliedMigrations[3], StringComparison.Ordinal);
        Assert.Empty(second.AppliedMigrations);
        await using var context = await Factory().CreateDbContextAsync(TestContext.Current.CancellationToken);
        var meta = await context.Meta.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(_kek.CheckValue, meta.KekCheck);
        Assert.Equal(_time.Now, meta.CreatedAt);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task TheDatabaseFileIsCreatedPrivate()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes.");

        await Initialize();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path));
    }

    [Fact]
    public async Task AnotherKekFailsLoudlyAtStartup()
    {
        await Initialize();
        using var other = Kek.Generate();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Initialize(other));

        Assert.Contains("does not match", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(other.Encode(), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_kek.Encode(), ex.Message, StringComparison.Ordinal);

        // The right key still opens it afterwards.
        await Initialize();
    }

    [Fact]
    public async Task ADatabaseWithValuesAndNoCheckValueIsRefused()
    {
        await Initialize();
        await using (var context = await Factory().CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            context.AccountKeys.Add(new AccountKeyRow
            {
                Id = AcmeAccountId.New(),
                Algorithm = JwsAlgorithm.ES256,
                Kty = "EC",
                Thumbprint = "t",
                CreatedAt = _time.Now,
                Sealed = [1],
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await context.Meta.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Initialize());

        Assert.Contains("damaged", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnumsAreStoredByStableNameAndIdsAsSixteenBytes()
    {
        await Initialize();
        var id = SecretId.New();
        await using (var context = await Factory().CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            context.Secrets.Add(new SecretRow
            {
                Id = id,
                Kind = SecretKind.TsigKey,
                MacAlgorithm = MacAlgorithm.HmacSha384,
                Version = 1,
                CreatedAt = _time.Now,
                UpdatedAt = _time.Now,
                Sealed = [1],
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var raw = await Factory().CreateDbContextAsync(TestContext.Current.CancellationToken);
        var kinds = await raw.Database.SqlQueryRaw<string>("SELECT \"Kind\" || '/' || \"MacAlgorithm\" || '/' || length(\"Id\") AS \"Value\" FROM \"Secrets\"")
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["tsig-key/hmac-sha384/16"], kinds);
    }

    [Fact]
    public async Task TheSchemaRefusesRowsThatBreakTheKindAndAlgorithmRules()
    {
        await Initialize();
        await using var context = await Factory().CreateDbContextAsync(TestContext.Current.CancellationToken);

        // A MAC kind without an algorithm, and an algorithm on a non-MAC kind.
        foreach (var (kind, algorithm) in new (SecretKind, MacAlgorithm?)[]
        {
            (SecretKind.TsigKey, null),
            (SecretKind.DnsApiToken, MacAlgorithm.HmacSha256),
        })
        {
            context.Secrets.Add(new SecretRow
            {
                Id = SecretId.New(),
                Kind = kind,
                MacAlgorithm = algorithm,
                Version = 1,
                CreatedAt = _time.Now,
                UpdatedAt = _time.Now,
                Sealed = [1],
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));
            context.ChangeTracker.Clear();
        }
    }

    [Fact]
    public async Task EveryConnectionOverwritesDeletedContent()
    {
        await Initialize();
        await using var context = await Factory().CreateDbContextAsync(TestContext.Current.CancellationToken);

        var secureDelete = await context.Database.SqlQueryRaw<int>("SELECT secure_delete AS \"Value\" FROM pragma_secure_delete")
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([1], secureDelete);
    }

    [Fact]
    public async Task OnlyOneMetaRowCanExist()
    {
        await Initialize();
        await using var context = await Factory().CreateDbContextAsync(TestContext.Current.CancellationToken);
        context.Meta.Add(new VaultMetaRow { Slot = 2, KekCheck = _kek.CheckValue, CreatedAt = _time.Now, UpdatedAt = _time.Now });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));
    }
}
