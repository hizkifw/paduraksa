using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Acme.Dns;
using Paduraksa.Core.Model;

namespace Paduraksa.Application.Issuance;

/// <summary>A source of DNS providers to be composed into the one <see cref="IDnsProviderFactory"/> of the process (see <see cref="DnsProviderServiceCollectionExtensions.AddDnsProviders"/>).</summary>
/// <param name="Create">Builds the factory once the container is built, so it can take the services it needs (the vault's handler factory) from it.</param>
public sealed record DnsProviderRegistration(Func<IServiceProvider, IDnsProviderFactory> Create);

/// <summary>
/// The <see cref="IDnsProviderFactory"/> the issuance services use: asks each registered factory in turn and takes the first that
/// knows the kind. A factory that does not know a kind throws <see cref="NotSupportedException"/> (the interface's contract), so
/// providers are added by registering a factory for them and nothing in the composition has to change when one is.
/// </summary>
public sealed class CompositeDnsProviderFactory : IDnsProviderFactory
{
    private readonly IReadOnlyList<IDnsProviderFactory> _factories;

    public CompositeDnsProviderFactory(IEnumerable<IDnsProviderFactory> factories)
    {
        ArgumentNullException.ThrowIfNull(factories);
        _factories = [.. factories];
    }

    /// <inheritdoc />
    public IDnsProvider Create(DnsProviderKind kind, SecretId credential)
    {
        foreach (IDnsProviderFactory factory in _factories)
        {
            try
            {
                return factory.Create(kind, credential);
            }
            catch (NotSupportedException)
            {
                // Not this factory's kind; the next one may know it.
            }
        }

        throw new NotSupportedException($"There is no DNS provider implementation for {kind}.");
    }
}

/// <summary>Adding DNS providers to the issuance services.</summary>
public static class DnsProviderServiceCollectionExtensions
{
    /// <summary>
    /// Makes the providers of <paramref name="factory"/> available to the issuance services (<see cref="IssuanceServiceCollectionExtensions.AddPaduraksaIssuance"/>
    /// composes every registered factory into the one <see cref="IDnsProviderFactory"/>). Call once per factory, before or after
    /// <c>AddPaduraksaIssuance</c>; a kind two factories know is served by the one registered first.
    /// </summary>
    public static IServiceCollection AddDnsProviders(this IServiceCollection services, Func<IServiceProvider, IDnsProviderFactory> factory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);
        return services.AddSingleton(new DnsProviderRegistration(factory));
    }
}
