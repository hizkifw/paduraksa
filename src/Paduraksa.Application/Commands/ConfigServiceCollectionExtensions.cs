using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Paduraksa.Application.Compilation;
using Paduraksa.Data;
using Paduraksa.Proxy.Yarp;

namespace Paduraksa.Application.Commands;

public static class ConfigServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ConfigService"/> as a singleton (the single writer), with <see cref="TimeProvider.System"/>,
    /// the allow-everything <see cref="IConfigCommandAuthorizer"/> and default <see cref="ConfigServiceOptions"/>
    /// unless those are already registered. The config database's context factory, an <see cref="IConfigCompiler"/>
    /// and the <see cref="SnapshotPublisher"/> must be registered too.
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
            sp.GetRequiredService<ConfigServiceOptions>()));
        return services;
    }
}
