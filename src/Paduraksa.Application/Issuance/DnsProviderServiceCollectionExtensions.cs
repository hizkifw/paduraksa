using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Paduraksa.Acme.Dns;
using Paduraksa.Core.Vault;

namespace Paduraksa.Application.Issuance;

public static class DnsProviderServiceCollectionExtensions
{
    /// <summary>
    /// Registers the built-in DNS-01 providers (Cloudflare, deSEC) as the <see cref="IDnsProviderFactory"/> that
    /// <see cref="IssuanceServiceCollectionExtensions.AddPaduraksaIssuance"/> and <see cref="DnsProviderService"/> use, and (unless turned off) the authoritative
    /// name-server <see cref="IDnsPropagationCheck"/> that replaces the fixed wait (PLAN §9). Needs the vault's
    /// <see cref="ISecretHttpHandlerFactory"/>: a provider is given a secret ID and that factory, never a token. Used when
    /// registered: <see cref="DnsProviderOptions"/>, <see cref="DnsPropagationOptions"/>, <see cref="TimeProvider"/>,
    /// <see cref="ILoggerFactory"/>. Anything already registered for the same service is kept.
    /// </summary>
    /// <param name="services">The collection.</param>
    /// <param name="propagation">
    /// The check's settings (bind <c>Paduraksa:Acme:DnsPropagation</c> into <see cref="DnsPropagationSettings"/>); the defaults when
    /// null. <see cref="DnsPropagationSettings.Enabled"/> false does not register the check, which keeps the fixed
    /// <see cref="AcmeIssuanceOptions.DnsPropagationDelay"/>, for a host that cannot send DNS queries to the zone's name servers.
    /// </param>
    /// <exception cref="ArgumentException">The settings are invalid.</exception>
    public static IServiceCollection AddPaduraksaDnsProviders(this IServiceCollection services, DnsPropagationSettings? propagation = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new DnsProviderOptions());
        services.TryAddSingleton<IDnsProviderFactory>(sp => new DnsProviderFactory(
            sp.GetRequiredService<ISecretHttpHandlerFactory>(),
            sp.GetRequiredService<DnsProviderOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILoggerFactory>()));
        propagation ??= new DnsPropagationSettings();
        if (propagation.Enabled)
        {
            services.TryAddSingleton(propagation.ToOptions());
            services.TryAddSingleton<IDnsPropagationCheck>(sp => new AuthoritativeDnsPropagationCheck(
                sp.GetRequiredService<DnsPropagationOptions>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetService<ILogger<AuthoritativeDnsPropagationCheck>>()));
        }

        return services;
    }
}
