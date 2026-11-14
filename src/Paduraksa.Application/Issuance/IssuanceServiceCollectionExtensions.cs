using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Paduraksa.Acme;
using Paduraksa.Acme.Dns;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Hosting;
using Paduraksa.Application.Identity;
using Paduraksa.Application.Notifications;
using Paduraksa.Core.Acme;
using Paduraksa.Core.Options;
using Paduraksa.Core.Vault;
using Paduraksa.Data;

namespace Paduraksa.Application.Issuance;

public static class IssuanceServiceCollectionExtensions
{
    /// <summary>Registers ACME issuance: the account and DNS provider services, the <see cref="AcmeIssuanceService"/>
    /// and the <see cref="AcmeRenewalScheduler"/> hosted service. Needs the <see cref="IAcmeClientProvider"/>, the
    /// <see cref="IPermissionEvaluator"/>, the vault's <see cref="ICertificateCatalog"/>, <see cref="ISecretStore"/> and
    /// <see cref="IJwsSigner"/>, and the <see cref="IProcessReadiness"/> signal. Used when registered: <see cref="IMacKeySource"/>
    /// (external account binding), <see cref="IDnsProviderFactory"/> and <see cref="IDnsPropagationCheck"/> (DNS-01; both come
    /// from <see cref="DnsProviderServiceCollectionExtensions.AddPaduraksaDnsProviders"/>, and without a factory a DNS-01
    /// request fails as not supported), <see cref="IBuiltInCertificates"/>.</summary>
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
        // The issuance service reads these live (the DNS-01 wait is a server setting); a composition that fixes them registers the plain options.
        services.TryAddSingleton(static sp => new LiveOptions<AcmeIssuanceOptions>(sp.GetRequiredService<AcmeIssuanceOptions>()));
        services.TryAddSingleton(sp => new AcmeAccountService(
            sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(),
            sp.GetRequiredService<ConfigService>(),
            sp.GetRequiredService<IAcmeClientProvider>(),
            sp.GetRequiredService<IJwsSigner>(),
            sp.GetService<IMacKeySource>(),
            sp.GetRequiredService<TimeProvider>(),
            Logger<AcmeAccountService>(sp),
            sp.GetRequiredService<IPermissionEvaluator>(),
            sp.GetRequiredService<ISecretStore>()));
        services.TryAddSingleton(sp => new DnsProviderService(
            sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(),
            sp.GetRequiredService<ConfigService>(),
            sp.GetRequiredService<ISecretStore>(),
            sp.GetService<IDnsProviderFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            Logger<DnsProviderService>(sp),
            sp.GetRequiredService<IPermissionEvaluator>()));
        services.TryAddSingleton(sp => new AcmeIssuanceService(
            sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(),
            sp.GetRequiredService<ConfigService>(),
            sp.GetRequiredService<CertificateService>(),
            sp.GetRequiredService<IAcmeClientProvider>(),
            sp.GetRequiredService<ICertificateIssuance>(),
            sp.GetRequiredService<IAcmeChallengeStore>(),
            sp.GetService<IDnsProviderFactory>(),
            sp.GetRequiredService<LiveOptions<AcmeIssuanceOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            Logger<AcmeIssuanceService>(sp),
            sp.GetRequiredService<IPermissionEvaluator>(),
            sp.GetRequiredService<ICertificateCatalog>(),
            sp.GetService<IDnsPropagationCheck>(),
            sp.GetService<IDnsChallengeNames>(),
            Random.Shared.NextDouble,
            sp.GetService<INotificationEvents>()));
        services.AddSingleton(sp => new AcmeRenewalScheduler(
            sp.GetRequiredService<AcmeIssuanceService>(),
            sp.GetRequiredService<AcmeIssuanceOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            Logger<AcmeRenewalScheduler>(sp),
            sp.GetRequiredService<IProcessReadiness>()));
        services.AddHostedService(sp => sp.GetRequiredService<AcmeRenewalScheduler>());
        return services;
    }

    private static ILogger<T> Logger<T>(IServiceProvider provider) =>
        (provider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger<T>();
}
