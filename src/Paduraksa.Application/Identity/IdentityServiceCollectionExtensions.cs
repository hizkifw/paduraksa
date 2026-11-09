using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Application.Commands;
using Paduraksa.Core.Vault;
using Paduraksa.Data;

namespace Paduraksa.Application.Identity;

public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// Registers the identity services and makes permissions decide who may run config commands and change certificates.
    /// It <b>replaces</b> the allow-all <see cref="IConfigCommandAuthorizer"/> that
    /// <see cref="ConfigServiceCollectionExtensions.AddPaduraksaConfigService"/> leaves behind, in whichever order the two are
    /// called, and replaces <see cref="Certificates.ICertificateOperationAuthorizer"/> the same way. Request simulations then need
    /// <c>simulate</c> (and <c>config:read</c> for a draft or a revision), and <see cref="Certificates.CertificateService"/> is given
    /// the <see cref="IPermissionEvaluator"/>. Needs the config database's context factory and the vault's
    /// <see cref="ITotpAuthenticator"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="dataDirectory">The data directory: <c>setup-token</c> lives in it.</param>
    /// <param name="options">The lockout, session and password settings; the defaults when null.</param>
    public static IServiceCollection AddPaduraksaIdentity(this IServiceCollection services, string dataDirectory, IdentityOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        options ??= new IdentityOptions();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddSingleton(new PasswordHasher(options.PasswordIterations));
        services.AddSingleton(new PasswordPolicy(options.MinPasswordLength));
        services.AddSingleton(sp => new IdentityAudit(sp.GetRequiredService<TimeProvider>(), LoggerFactory(sp)));
        services.AddSingleton<IPermissionEvaluator>(sp => new PermissionEvaluator(
            sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(), sp.GetRequiredService<TimeProvider>()));

        services.RemoveAll<IConfigCommandAuthorizer>();
        services.AddSingleton<IConfigCommandAuthorizer>(sp => new PermissionConfigCommandAuthorizer(sp.GetRequiredService<IPermissionEvaluator>()));

        services.RemoveAll<Certificates.ICertificateOperationAuthorizer>();
        services.AddSingleton<Certificates.ICertificateOperationAuthorizer>(sp => new PermissionCertificateOperationAuthorizer(sp.GetRequiredService<IPermissionEvaluator>()));

        services.RemoveAll<Simulation.ISimulationAuthorizer>();
        services.AddSingleton<Simulation.ISimulationAuthorizer>(sp => new PermissionSimulationAuthorizer(sp.GetRequiredService<IPermissionEvaluator>()));

        services.AddSingleton(sp => new UserService(
            sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IdentityOptions>(),
            sp.GetRequiredService<PasswordHasher>(),
            sp.GetRequiredService<PasswordPolicy>(),
            sp.GetRequiredService<IdentityAudit>(),
            sp.GetRequiredService<IPermissionEvaluator>(),
            sp.GetService<ITotpAuthenticator>(),
            LoggerFactory(sp).CreateLogger<UserService>()));
        services.AddSingleton(sp => new SessionService(
            sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IdentityOptions>(),
            sp.GetRequiredService<IdentityAudit>(),
            sp.GetRequiredService<IPermissionEvaluator>()));
        services.AddSingleton(sp => new TwoFactorService(
            sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IdentityOptions>(),
            sp.GetRequiredService<PasswordHasher>(),
            sp.GetRequiredService<IdentityAudit>(),
            sp.GetRequiredService<IPermissionEvaluator>(),
            sp.GetRequiredService<ITotpAuthenticator>(),
            LoggerFactory(sp).CreateLogger<TwoFactorService>()));
        services.AddSingleton(sp => new LoginService(
            sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IdentityOptions>(),
            sp.GetRequiredService<PasswordHasher>(),
            sp.GetRequiredService<IdentityAudit>(),
            sp.GetRequiredService<SessionService>(),
            sp.GetRequiredService<ITotpAuthenticator>()));
        services.AddSingleton(sp => new ApiTokenService(
            sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IdentityOptions>(),
            sp.GetRequiredService<IdentityAudit>(),
            sp.GetRequiredService<IPermissionEvaluator>()));
        services.AddSingleton(sp => new SetupService(
            sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<PasswordHasher>(),
            sp.GetRequiredService<IdentityAudit>(),
            sp.GetRequiredService<UserService>(),
            dataDirectory,
            LoggerFactory(sp).CreateLogger<SetupService>()));
        return services;
    }

    private static ILoggerFactory LoggerFactory(IServiceProvider sp) => sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
}
