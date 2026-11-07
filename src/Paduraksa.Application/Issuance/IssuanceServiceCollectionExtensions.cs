using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Acme;
using Paduraksa.Acme.Dns;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Core.Acme;
using Paduraksa.Core.Vault;
using Paduraksa.Data;

namespace Paduraksa.Application.Issuance;

public static class IssuanceServiceCollectionExtensions
{
    /// <summary>
    /// Registers ACME issuance (PLAN §9.3): the account and DNS provider services, the <see cref="AcmeIssuanceService"/> and
    /// the <see cref="AcmeRenewalScheduler"/> hosted service, with the client provider that signs through the vault.
    /// Needs <see cref="ConfigServiceCollectionExtensions.AddPaduraksaConfigService"/>, the vault's <see cref="IJwsSigner"/>,
    /// <see cref="ICertificateIssuance"/> and <see cref="ISecretStore"/>, and an <see cref="IAcmeChallengeStore"/>. Used when
    /// registered: <see cref="IMacKeySource"/> (external account binding), <see cref="IDnsProviderFactory"/> and
    /// <see cref="IDnsPropagationCheck"/> (DNS-01; both come from <see cref="DnsProviderServiceCollectionExtensions.AddPaduraksaDnsProviders"/>,
    /// and without a factory a DNS-01 request fails as not supported), <see cref="ICertificateCatalog"/> (repairs an attach whose record a crash lost),
    /// <see cref="AcmeIssuanceOptions"/> and <see cref="AcmeClientOptions"/>, and an <see cref="IAcmeClientProvider"/> of your own.
    /// </summary>
    public static IServiceCollection AddPaduraksaIssuance(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(d => d.ServiceType == typeof(AcmeRenewalScheduler)))
        {
            // Already added: one scheduler, however many times this is called.
            return services;
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new AcmeIssuanceOptions());
        services.TryAddSingleton<IAcmeClientProvider>(sp => new AcmeClientProvider(
            sp.GetRequiredService<IJwsSigner>(),
            sp.GetService<IMacKeySource>(),
            sp.GetService<AcmeClientOptions>(),
            handlerFactory: null,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILoggerFactory>()));
        services.TryAddSingleton(sp => new AcmeAccountService(
            sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(),
            sp.GetRequiredService<ConfigService>(),
            sp.GetRequiredService<IAcmeClientProvider>(),
            sp.GetRequiredService<IJwsSigner>(),
            sp.GetService<IMacKeySource>(),
            sp.GetRequiredService<TimeProvider>(),
            Logger<AcmeAccountService>(sp)));
        services.TryAddSingleton(sp => new DnsProviderService(
            sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(),
            sp.GetRequiredService<ConfigService>(),
            sp.GetRequiredService<ISecretStore>(),
            sp.GetService<IDnsProviderFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            Logger<DnsProviderService>(sp)));
        services.TryAddSingleton(sp => new AcmeIssuanceService(
            sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(),
            sp.GetRequiredService<ConfigService>(),
            sp.GetRequiredService<CertificateService>(),
            sp.GetRequiredService<IAcmeClientProvider>(),
            sp.GetRequiredService<ICertificateIssuance>(),
            sp.GetRequiredService<IAcmeChallengeStore>(),
            sp.GetService<IDnsProviderFactory>(),
            sp.GetRequiredService<AcmeIssuanceOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            Logger<AcmeIssuanceService>(sp),
            catalog: sp.GetService<ICertificateCatalog>(),
            propagation: sp.GetService<IDnsPropagationCheck>()));
        services.AddSingleton(sp => new AcmeRenewalScheduler(
            sp.GetRequiredService<AcmeIssuanceService>(),
            sp.GetRequiredService<AcmeIssuanceOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            Logger<AcmeRenewalScheduler>(sp)));
        services.AddHostedService(sp => sp.GetRequiredService<AcmeRenewalScheduler>());
        return services;
    }

    private static ILogger<T> Logger<T>(IServiceProvider provider) =>
        (provider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger<T>();
}
