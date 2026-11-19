using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Paduraksa.Tests.TypedIdSample;
using Paduraksa.TypedIds.EntityFrameworkCore;

namespace Paduraksa.TypedIds.Tests;

/// <summary>Model-only checks on PostgreSQL's type mappings: nothing connects, so these run everywhere.</summary>
public sealed class NativeGuidColumnTests
{
    private static IModel NativeModel() => ModelOf(new NativeGuidContext(new DbContextOptionsBuilder<NativeGuidContext>().UseNpgsql("Host=unused").Options));

    private static IModel BinaryModel() => ModelOf(new BinaryGuidContext(new DbContextOptionsBuilder<BinaryGuidContext>().UseNpgsql("Host=unused").Options));

    // The design-time model carries the relational annotations; the runtime model has them stripped.
    private static IModel ModelOf(DbContext context)
    {
        using (context)
        {
            return context.GetService<IDesignTimeModel>().Model;
        }
    }

    private static IProperty Column(IModel model, Type entity, string property) => model.FindEntityType(entity)!.FindProperty(property)!;

    [Fact]
    public void NativeMapsGuidBackedIdsToUuidAndInt64IdsToBigint()
    {
        var model = NativeModel();

        Assert.Equal("uuid", Column(model, typeof(Host), nameof(Host.Id)).GetColumnType());              // UuidV7 key
        Assert.Equal("uuid", Column(model, typeof(Host), nameof(Host.ParentId)).GetColumnType());        // nullable UuidV7 FK
        Assert.Equal("uuid", Column(model, typeof(Challenge), nameof(Challenge.Id)).GetColumnType());    // UuidV4 key
        Assert.Equal("uuid", Column(model, typeof(Challenge), nameof(Challenge.HostId)).GetColumnType());
        Assert.Equal("bigint", Column(model, typeof(AuditEvent), nameof(AuditEvent.Id)).GetColumnType());
        Assert.Equal("bigint", Column(model, typeof(AuditEvent), nameof(AuditEvent.PreviousId)).GetColumnType());
        var origin = model.FindEntityType(typeof(Route))!.FindComplexProperty(nameof(Route.Origin))!.ComplexType;
        Assert.Equal("uuid", origin.FindProperty(nameof(RouteOrigin.CopiedFromRouteId))!.GetColumnType()); // column-mapped complex type
    }

    [Fact]
    public void NativeKeepsIdsInsideJsonAndPrimitiveCollectionsAsTheirCanonicalString()
    {
        var model = NativeModel();

        var settings = model.FindEntityType(typeof(Host))!.FindComplexProperty(nameof(Host.Settings))!.ComplexType;
        foreach (var name in new[] { nameof(HostSettings.DefaultRouteId), nameof(HostSettings.FallbackHostId), nameof(HostSettings.LastAuditId) })
        {
            Assert.Equal(typeof(string), settings.FindProperty(name)!.GetValueConverter()!.ProviderClrType);
        }

        var related = Column(model, typeof(Route), nameof(Route.RelatedHostIds));
        Assert.Equal(typeof(string), related.GetElementType()!.GetValueConverter()!.ProviderClrType);
    }

    [Fact]
    public void BinaryMapsGuidBackedIdsToByteaOnPostgreSql()
    {
        var model = BinaryModel();

        Assert.Equal("bytea", Column(model, typeof(Host), nameof(Host.Id)).GetColumnType());
        Assert.Equal("bytea", Column(model, typeof(Challenge), nameof(Challenge.Id)).GetColumnType());
        Assert.Equal("bigint", Column(model, typeof(AuditEvent), nameof(AuditEvent.Id)).GetColumnType());
    }

    [Fact]
    public void BinaryIsTheDefault()
    {
        using var context = new DefaultGuidContext(new DbContextOptionsBuilder<DefaultGuidContext>().UseNpgsql("Host=unused").Options);

        Assert.Equal("bytea", Column(context.GetService<IDesignTimeModel>().Model, typeof(Host), nameof(Host.Id)).GetColumnType());
    }

    [Fact]
    public void TwoContextsInOneProcessKeepTheirOwnSettingWhicheverModelIsBuiltFirst()
    {
        // TypedIdMapping is a process-wide cache per CLR type: the setting must not leak through it, in either order.
        foreach (var nativeFirst in new[] { true, false })
        {
            IModel native, binary;
            if (nativeFirst)
            {
                native = NativeModel();
                binary = BinaryModel();
            }
            else
            {
                binary = BinaryModel();
                native = NativeModel();
            }

            Assert.Equal("uuid", Column(native, typeof(Host), nameof(Host.Id)).GetColumnType());
            Assert.Equal("bytea", Column(binary, typeof(Host), nameof(Host.Id)).GetColumnType());
        }
    }

    [Fact]
    public async Task SqliteWithNativeStoresA36CharacterString()
    {
        // The documented reason to keep the default there: SQLite has no Guid type, so Native is a 36-character string.
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = new SqliteNativeContext(new DbContextOptionsBuilder<SqliteNativeContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var id = HostId.New();
        context.Add(new Host { Id = id, Name = "h" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT typeof(Id), length(Id) FROM Host";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(("text", 36L), (reader.GetString(0), reader.GetInt64(1)));
        Assert.Equal(id, (await context.Set<Host>().SingleAsync(TestContext.Current.CancellationToken)).Id);
    }

    private sealed class DefaultGuidContext(DbContextOptions<DefaultGuidContext> options) : DbContext(options)
    {
        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
            configurationBuilder.AddTypedIdConventions();

        protected override void OnModelCreating(ModelBuilder modelBuilder) => NativeGuidModel.Configure(modelBuilder);
    }

    private sealed class SqliteNativeContext(DbContextOptions<SqliteNativeContext> options) : DbContext(options)
    {
        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
            configurationBuilder.AddTypedIdConventions(options => options.GuidColumns = TypedIdGuidColumns.Native);

        protected override void OnModelCreating(ModelBuilder modelBuilder) => NativeGuidModel.Configure(modelBuilder);
    }
}
