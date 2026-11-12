using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Application.Audit;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.Drafts;
using Paduraksa.Application.Identity;
using Paduraksa.Application.LastKnownGood;
using Paduraksa.Application.Revisions;
using Paduraksa.Application.Simulation;
using Paduraksa.Application.Yaml;
using Paduraksa.Core.Vault;
using Paduraksa.Data;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Proxy.Yarp;

namespace Paduraksa.Application.Commands;

public static class ConfigServiceCollectionExtensions
{
    /// <summary>Registers <see cref="ConfigService"/> (the single writer) with the services that share its writer: the
    /// <see cref="CertificateService"/>, the <see cref="RequestSimulator"/>, the <see cref="DraftService"/>, the <see cref="ConfigImportService"/> and the read-only <see cref="RevisionQueryService"/>
    /// and <see cref="AuditQueryService"/>. Needs the config database's context factory, an <see cref="IConfigCompiler"/>, the
    /// <see cref="SnapshotPublisher"/>, the <see cref="IPermissionEvaluator"/> (<c>AddPaduraksaIdentity</c>), the log factory, a
    /// <see cref="LastKnownGoodFile"/>, the vault's <see cref="IServerCertificateSource"/>, <see cref="ICertificateCatalog"/> and <see cref="IPasswordCredentials"/>, the
    /// proxy's <see cref="IUpstreamHealth"/>, <see cref="AcmeChallengeStore"/>, <see cref="ListenerFacts"/> and
    /// <see cref="ProofOfWorkRuntime"/>. <see cref="IBuiltInCertificates"/> is used when registered; <see cref="TimeProvider.System"/> and
    /// default <see cref="ConfigServiceOptions"/> unless registered.</summary>
    public static IServiceCollection AddPaduraksaConfigService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new ConfigServiceOptions());
        services.TryAddSingleton(sp => new ConfigService(
            sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(),
            sp.GetRequiredService<IConfigCompiler>(),
            sp.GetRequiredService<SnapshotPublisher>(),
            sp.GetRequiredService<IPermissionEvaluator>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<ConfigServiceOptions>(),
            sp.GetRequiredService<LastKnownGoodFile>(),
            sp.GetRequiredService<IServerCertificateSource>(),
            sp.GetService<IBuiltInCertificates>(),
            sp.GetRequiredService<ICertificateCatalog>(),
            sp.GetRequiredService<IPasswordCredentials>()));
        services.TryAddSingleton(sp => new CertificateService(
            sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(),
            sp.GetRequiredService<ConfigService>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<CertificateService>(),
            sp.GetRequiredService<IPermissionEvaluator>(),
            sp.GetService<IBuiltInCertificates>()));
        services.TryAddSingleton(sp => new RequestSimulator(
            sp.GetRequiredService<ConfigService>(),
            sp.GetRequiredService<SnapshotPublisher>().Store,
            sp.GetRequiredService<IPermissionEvaluator>(),
            sp.GetRequiredService<IUpstreamHealth>(),
            sp.GetRequiredService<AcmeChallengeStore>(),
            sp.GetRequiredService<ListenerFacts>(),
            sp.GetRequiredService<ProofOfWorkRuntime>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<RequestSimulator>(),
            RequestSimulator.BusyWait,
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton(sp => new DraftService(
            sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(),
            sp.GetRequiredService<ConfigService>(),
            sp.GetRequiredService<IPasswordCredentials>(),
            sp.GetRequiredService<IPermissionEvaluator>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>()));
        services.TryAddSingleton(sp => new RevisionQueryService(sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>()));
        services.TryAddSingleton(sp => new AuditQueryService(sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>()));
        services.TryAddSingleton(sp => new ConfigImportService(
            sp.GetRequiredService<ConfigService>(),
            sp.GetRequiredService<RevisionQueryService>(),
            sp.GetRequiredService<IPasswordCredentials>(),
            sp.GetRequiredService<IPermissionEvaluator>()));
        return services;
    }
}
