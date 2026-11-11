using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Data.Audit;
using Paduraksa.Data.Certificates;
using Paduraksa.Data.Conversion;
using Paduraksa.Data.Identity;
using Paduraksa.Data.Issuance;
using Paduraksa.Data.Revisions;
using Paduraksa.Storage;
using Paduraksa.Storage.Ids;

namespace Paduraksa.Data;

/// <summary>
/// The config database (<c>data/paduraksa.db</c>): revisions (the config document is the only copy of the config),
/// audit, and the certificate inventory (<see cref="Certificates.CertificateRow"/>) with its ACME accounts, orders and
/// DNS providers (<see cref="Issuance.AcmeOrderRow"/>), all of it operational metadata outside the config document. Entities are
/// internal to Data and Application; nothing outside them sees a row type. Create contexts from the registered
/// <see cref="IDbContextFactory{TContext}"/>; the schema comes from migrations, applied at startup by
/// <see cref="ConfigDatabase.InitializeAsync"/>.
/// </summary>
public sealed class ConfigDbContext(DbContextOptions<ConfigDbContext> options) : DbContext(options)
{
    internal DbSet<ConfigRevision> ConfigRevisions => Set<ConfigRevision>();

    internal DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

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
