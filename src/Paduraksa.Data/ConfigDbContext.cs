using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Model;
using Paduraksa.Data.Audit;
using Paduraksa.Data.Ids;
using Paduraksa.Data.Revisions;
using Paduraksa.Data.Storage;

namespace Paduraksa.Data;

/// <summary>
/// The config database (<c>data/paduraksa.db</c>, PLAN §5): revisions, audit and, from the next M2 wave, the
/// projection of the latest revision's config entities. Entities are internal to Data and Application; nothing
/// outside them sees a row type. Create contexts from the registered <see cref="IDbContextFactory{TContext}"/>
/// (<see cref="Sqlite.PaduraksaSqlite.AddPaduraksaDbContextFactory{TContext}"/>); the schema comes from migrations
/// (applied at startup by <c>ConfigDatabase.InitializeAsync</c>).
/// </summary>
/// <remarks>
/// Adding an entity: a <see cref="DbSet{TEntity}"/> here, an <see cref="IEntityTypeConfiguration{TEntity}"/> next to
/// the entity, applied in <see cref="OnModelCreating"/>, then a migration (README, "Database migrations").
/// </remarks>
public sealed class ConfigDbContext(DbContextOptions<ConfigDbContext> options) : DbContext(options)
{
    internal DbSet<ConfigRevision> ConfigRevisions => Set<ConfigRevision>();

    internal DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.AddTypedIdConventions();
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTimestampConverter>();
        configurationBuilder.Properties<ActorKind>().HaveConversion<ActorKindConverter>();
        configurationBuilder.Properties<Channel>().HaveConversion<ChannelConverter>();
        configurationBuilder.Properties<AuditOutcome>().HaveConversion<AuditOutcomeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new ConfigRevisionConfiguration());
        modelBuilder.ApplyConfiguration(new AuditEventConfiguration());
    }
}
