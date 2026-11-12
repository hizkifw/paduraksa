using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Paduraksa.Application.Identity;
using Paduraksa.Core.Vault;
using Paduraksa.Data;

namespace Paduraksa.Application.Backup;

public static class BackupServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="BackupService"/> over the data directory's config database and last-known-good file. Needs the vault
    /// (<see cref="IVaultBackup"/>), the config database's context factory and the identity services (the permission check and the audit).
    /// </summary>
    public static IServiceCollection AddPaduraksaBackup(this IServiceCollection services, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(BackupLocations.In(dataDirectory));
        services.AddSingleton(sp => new BackupService(
            sp.GetRequiredService<IVaultBackup>(),
            sp.GetRequiredService<IPermissionEvaluator>(),
            sp.GetRequiredService<IdentityAudit>(),
            sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(),
            sp.GetRequiredService<BackupLocations>(),
            sp.GetRequiredService<TimeProvider>(),
            (sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger<BackupService>()));
        return services;
    }
}
