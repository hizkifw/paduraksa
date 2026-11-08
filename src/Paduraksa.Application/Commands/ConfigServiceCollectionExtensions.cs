using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Application.Audit;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.Identity;
using Paduraksa.Application.LastKnownGood;
using Paduraksa.Application.Revisions;
using Paduraksa.Application.Simulation;
using Paduraksa.Core.Vault;
using Paduraksa.Data;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Proxy.Yarp;

namespace Paduraksa.Application.Commands;

public static class ConfigServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ConfigService"/> as a singleton (the single writer), with <see cref="TimeProvider.System"/>,
    /// the allow-everything <see cref="IConfigCommandAuthorizer"/> (replaced by <c>AddPaduraksaIdentity</c>) and default <see cref="ConfigServiceOptions"/>
    /// unless those are already registered. The config database's context factory, an <see cref="IConfigCompiler"/>
    /// and the <see cref="SnapshotPublisher"/> must be registered too; a <see cref="LastKnownGoodFile"/>, an
    /// <see cref="IServerCertificateSource"/> and <see cref="IBuiltInCertificates"/> are used when registered. Also
    /// registers the read-only <see cref="RevisionQueryService"/> and <see cref="AuditQueryService"/> of the surfaces, the <see cref="CertificateService"/>, which shares the service's writer, and the <see cref="RequestSimulator"/>
    /// (PLAN §7.7), which uses the live snapshot (the publisher's store) and, when registered, <see cref="IUpstreamHealth"/>, an
    /// <see cref="ISimulationAuthorizer"/>, the <see cref="AcmeChallengeStore"/> and the public listeners' <see cref="ListenerFacts"/>.
    /// Drafts and revisions are judged through the vault's <see cref="ICertificateCatalog"/> when one is registered.
    /// </summary>
    public static IServiceCollection AddPaduraksaConfigService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IConfigCommandAuthorizer>(AllowAllConfigCommands.Instance);
        services.TryAddSingleton(new ConfigServiceOptions());
        services.TryAddSingleton(sp => new ConfigService(
            sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(),
            sp.GetRequiredService<IConfigCompiler>(),
            sp.GetRequiredService<SnapshotPublisher>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILoggerFactory>(),
            sp.GetRequiredService<IConfigCommandAuthorizer>(),
            sp.GetRequiredService<ConfigServiceOptions>(),
            sp.GetService<LastKnownGoodFile>(),
            sp.GetService<IServerCertificateSource>(),
            sp.GetService<IBuiltInCertificates>(),
            sp.GetService<ICertificateCatalog>()));
        services.TryAddSingleton(sp => new CertificateService(
            sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(),
            sp.GetRequiredService<ConfigService>(),
            sp.GetRequiredService<TimeProvider>(),
            (sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger<CertificateService>(),
            sp.GetService<IBuiltInCertificates>(),
            sp.GetService<IPermissionEvaluator>()));
        services.TryAddSingleton(sp => new RequestSimulator(
            sp.GetRequiredService<ConfigService>(),
            sp.GetRequiredService<SnapshotPublisher>().Store,
            sp.GetService<IUpstreamHealth>(),
            sp.GetService<ISimulationAuthorizer>(),
            sp.GetService<AcmeChallengeStore>(),
            sp.GetService<ListenerFacts>()));
        services.TryAddSingleton(sp => new RevisionQueryService(sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>()));
        services.TryAddSingleton(sp => new AuditQueryService(sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>()));
        return services;
    }
}
