using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Application.Certificates;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.Identity;
using Paduraksa.Application.LastKnownGood;
using Paduraksa.Core.Vault;
using Paduraksa.Data;
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
    /// registers the <see cref="CertificateService"/>, which shares the service's writer.
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
            sp.GetService<IBuiltInCertificates>()));
        services.TryAddSingleton(sp => new CertificateService(
            sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(),
            sp.GetRequiredService<ConfigService>(),
            sp.GetRequiredService<TimeProvider>(),
            (sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger<CertificateService>(),
            sp.GetService<IBuiltInCertificates>(),
            sp.GetService<IPermissionEvaluator>()));
        return services;
    }
}
