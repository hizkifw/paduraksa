using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Paduraksa.Data.Ids;
using static Paduraksa.Tests.Data.SampleDatabase;

namespace Paduraksa.Tests.Data;

/// <summary>Edge cases of the convention: overrides, inheritance, unmappable members and unsupported shapes.</summary>
public sealed class TypedIdConventionTests
{
    [Fact]
    public async Task ExplicitConfigurationWinsOverTheConvention()
    {
        await using var model = await ModelDatabase.CreateAsync(m => m.Entity<AuditEvent>(audit =>
        {
            audit.Ignore(a => a.PreviousId);
            audit.Property(a => a.TargetHostId).HasConversion(new TypedIdToStringConverter<HostId>());
        }));
        var target = HostId.New();
        await model.SaveAsync(new AuditEvent { Id = AuditEventId.New(), Action = "a", TargetHostId = target });

        Assert.Equal(["text", target.ToString()], (await model.QueryAsync("SELECT typeof(TargetHostId), TargetHostId FROM AuditEvent")).Single());
    }

    [Fact]
    public void WithoutTheConventionsTypedIdsAreNotMappable()
    {
        // Documents why AddTypedIdConventions is required: EF has no idea what a typed ID is on its own.
        using var context = new ModelContext(m => m.Entity<Challenge>(), conventions: false);

        var error = Assert.Throws<InvalidOperationException>(() => context.Model);
        Assert.Contains("HostId", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TablePerTypeDerivedKeyKeepsItsConverter()
    {
        // The TPT link (derived Id -> base Id) rebuilds the derived key without the convention's converter; the
        // finalizing pass restores it.
        await using var model = await ModelDatabase.CreateAsync(m =>
        {
            m.Entity<Policy>().UseTptMappingStrategy();
            m.Entity<RateLimitPolicy>();
        });
        var policy = new RateLimitPolicy { Id = HostId.New(), RouteId = RouteId.New(), Limit = 10 };
        await model.SaveAsync(policy);

        Assert.Equal(["blob", 16L, "blob"], (await model.QueryAsync("SELECT typeof(Id), length(Id), typeof(RouteId) FROM RateLimitPolicy")).Single());
        await using var context = model.NewContext();
        var loaded = Assert.IsType<RateLimitPolicy>(await context.Set<Policy>().SingleAsync(p => p.Id == policy.Id, Ct));
        Assert.Equal(policy.RouteId, loaded.RouteId);
    }

    [Fact]
    public async Task OnlyPropertiesWithASetterAreMappedLikeEfDoesForGuid()
    {
        await using var model = await ModelDatabase.CreateAsync(m => m.Entity<Computed>());
        await using var context = model.NewContext();
        var entity = context.Model.FindEntityType(typeof(Computed))!;

        Assert.Null(entity.FindProperty(nameof(Computed.Alias)));            // `=> Id`: computed, no setter
        Assert.NotNull(entity.FindProperty(nameof(Computed.PrivateSet)));    // private setter
        Assert.NotNull(entity.FindProperty(nameof(Computed.Id)));            // setter declared on the base type
        var saved = new Computed(RouteId.New()) { PrivateSetValue = RouteId.New() };
        await model.SaveAsync(saved);
        await using var read = model.NewContext();
        var loaded = await read.Set<Computed>().SingleAsync(Ct);
        Assert.Equal((saved.Id, saved.PrivateSet), (loaded.Id, loaded.PrivateSet));
    }

    [Fact]
    public void OwnedTypeMappedToJsonIsRejected()
    {
        using var context = new ModelContext(m => m.Entity<Owner>().OwnsOne(o => o.Blob, b => b.ToJson()));

        var error = Assert.Throws<InvalidOperationException>(() => context.Model);
        Assert.Contains("ComplexProperty(...).ToJson()", error.Message, StringComparison.Ordinal);
    }

    public class Policy
    {
        public HostId Id { get; set; }
    }

    public sealed class RateLimitPolicy : Policy
    {
        public RouteId? RouteId { get; set; }

        public int Limit { get; set; }
    }

    public abstract class WithIdSetter
    {
        public virtual RouteId Id { get; protected set; }
    }

    public sealed class Computed : WithIdSetter
    {
        public Computed(RouteId id) => base.Id = id;

        private Computed()
        {
        }

        public override RouteId Id => base.Id;

        public RouteId Alias => Id;

        public RouteId PrivateSet { get; private set; }

        public RouteId PrivateSetValue
        {
            set => PrivateSet = value;
        }
    }

    public sealed class Owner
    {
        public HostId Id { get; set; }

        public OwnedBlob Blob { get; set; } = new();
    }

    public sealed class OwnedBlob
    {
        public RouteId RouteId { get; set; }
    }

    /// <summary>A context whose model is built from a delegate (no model caching, so every test gets its own).</summary>
    private sealed class ModelContext(Action<ModelBuilder> configure, bool conventions = true, SqliteConnection? connection = null) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (connection is null)
            {
                optionsBuilder.UseSqlite("Data Source=:memory:");
            }
            else
            {
                optionsBuilder.UseSqlite(connection);
            }

            optionsBuilder.EnableServiceProviderCaching(false);
        }

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            if (conventions)
            {
                configurationBuilder.AddTypedIdConventions();
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) => configure(modelBuilder);
    }

    private sealed class ModelDatabase(SqliteConnection connection, Action<ModelBuilder> configure) : IAsyncDisposable
    {
        public static async Task<ModelDatabase> CreateAsync(Action<ModelBuilder> configure)
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync(Ct);
            var database = new ModelDatabase(connection, configure);
            await using var context = database.NewContext();
            await context.Database.EnsureCreatedAsync(Ct);
            return database;
        }

        public ModelContext NewContext() => new(configure, connection: connection);

        public async Task SaveAsync(params object[] entities)
        {
            await using var context = NewContext();
            context.AddRange(entities);
            await context.SaveChangesAsync(Ct);
        }

        public async Task<List<object?[]>> QueryAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(Ct);
            var rows = new List<object?[]>();
            while (await reader.ReadAsync(Ct))
            {
                var row = new object?[reader.FieldCount];
                reader.GetValues(row);
                rows.Add(row);
            }

            return rows;
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
