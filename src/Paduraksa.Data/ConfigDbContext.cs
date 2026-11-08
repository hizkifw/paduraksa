using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Data.Audit;
using Paduraksa.Data.Certificates;
using Paduraksa.Data.Conversion;
using Paduraksa.Data.Identity;
using Paduraksa.Data.Issuance;
using Paduraksa.Data.Projection;
using Paduraksa.Data.Revisions;
using Paduraksa.Storage;
using Paduraksa.Storage.Ids;

namespace Paduraksa.Data;

/// <summary>
/// The config database (<c>data/paduraksa.db</c>, PLAN §5): revisions, audit, the projection of the latest
/// revision's config entities (<see cref="Projection.GlobalSettingsRow"/>) and the certificate inventory
/// (<see cref="Certificates.CertificateRow"/>) with its ACME accounts, orders and DNS providers (<see cref="Issuance.AcmeOrderRow"/>), operational metadata outside the config document. Entities are internal to Data and Application; nothing
/// outside them sees a row type. Create contexts from the registered <see cref="IDbContextFactory{TContext}"/>
/// (<see cref="Paduraksa.Storage.Sqlite.PaduraksaSqlite.AddPaduraksaDbContextFactory{TContext}"/>); the schema comes from migrations
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

    internal DbSet<GlobalSettingsRow> GlobalSettings => Set<GlobalSettingsRow>();

    internal DbSet<ProfileRow> Profiles => Set<ProfileRow>();

    internal DbSet<UpstreamRow> Upstreams => Set<UpstreamRow>();

    internal DbSet<DestinationRow> Destinations => Set<DestinationRow>();

    internal DbSet<HostRow> Hosts => Set<HostRow>();

    internal DbSet<HostNameRow> HostNames => Set<HostNameRow>();

    internal DbSet<RouteRow> Routes => Set<RouteRow>();

    internal DbSet<CertificateRow> Certificates => Set<CertificateRow>();

    internal DbSet<CertificateNameRow> CertificateNames => Set<CertificateNameRow>();

    internal DbSet<UserRow> Users => Set<UserRow>();

    internal DbSet<RoleAssignmentRow> RoleAssignments => Set<RoleAssignmentRow>();

    internal DbSet<ApiTokenRow> ApiTokens => Set<ApiTokenRow>();

    internal DbSet<SessionRow> Sessions => Set<SessionRow>();

    internal DbSet<RecoveryCodeRow> RecoveryCodes => Set<RecoveryCodeRow>();

    internal DbSet<AcmeAccountRow> AcmeAccounts => Set<AcmeAccountRow>();

    internal DbSet<AcmeOrderRow> AcmeOrders => Set<AcmeOrderRow>();

    internal DbSet<DnsProviderRow> DnsProviders => Set<DnsProviderRow>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.AddTypedIdConventions();
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTimestampConverter>();
        configurationBuilder.Properties<ActorKind>().HaveConversion<ActorKindConverter>();
        configurationBuilder.Properties<Channel>().HaveConversion<ChannelConverter>();
        configurationBuilder.Properties<AuditOutcome>().HaveConversion<AuditOutcomeConverter>();
        configurationBuilder.Properties<CertificateSource>().HaveConversion<CertificateSourceConverter>();
        configurationBuilder.Properties<CertificateStatus>().HaveConversion<CertificateStatusConverter>();
        configurationBuilder.Properties<Role>().HaveConversion<RoleConverter>();
        configurationBuilder.Properties<AcmeAccountStatus>().HaveConversion<AcmeAccountStatusConverter>();
        configurationBuilder.Properties<AcmeOrderStatus>().HaveConversion<AcmeOrderStatusConverter>();
        configurationBuilder.Properties<AcmeChallengeType>().HaveConversion<AcmeChallengeTypeConverter>();
        configurationBuilder.Properties<DnsProviderKind>().HaveConversion<DnsProviderKindConverter>();
        configurationBuilder.Properties<JwsAlgorithm>().HaveConversion<JwsAlgorithmConverter>();
        configurationBuilder.Properties<KeySpec>().HaveConversion<KeySpecConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new ConfigRevisionConfiguration());
        modelBuilder.ApplyConfiguration(new AuditEventConfiguration());
        modelBuilder.ApplyConfiguration(new GlobalSettingsRowConfiguration());
        modelBuilder.ApplyConfiguration(new ProfileRowConfiguration());
        modelBuilder.ApplyConfiguration<UpstreamRow>(new UpstreamRowConfiguration());
        modelBuilder.ApplyConfiguration<DestinationRow>(new UpstreamRowConfiguration());
        modelBuilder.ApplyConfiguration<HostRow>(new HostRowConfiguration());
        modelBuilder.ApplyConfiguration<HostNameRow>(new HostRowConfiguration());
        modelBuilder.ApplyConfiguration<RouteRow>(new HostRowConfiguration());
        modelBuilder.ApplyConfiguration<CertificateRow>(new CertificateRowConfiguration());
        modelBuilder.ApplyConfiguration<CertificateNameRow>(new CertificateRowConfiguration());
        modelBuilder.ApplyConfiguration(new AcmeAccountRowConfiguration());
        modelBuilder.ApplyConfiguration(new DnsProviderRowConfiguration());
        modelBuilder.ApplyConfiguration(new AcmeOrderRowConfiguration());
        IdentityConfiguration identity = new();
        modelBuilder.ApplyConfiguration<UserRow>(identity);
        modelBuilder.ApplyConfiguration<RoleAssignmentRow>(identity);
        modelBuilder.ApplyConfiguration<ApiTokenRow>(identity);
        modelBuilder.ApplyConfiguration<SessionRow>(identity);
        modelBuilder.ApplyConfiguration<RecoveryCodeRow>(identity);
    }
}
