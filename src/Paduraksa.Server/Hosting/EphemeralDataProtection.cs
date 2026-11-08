using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// The public app's Data Protection: YARP's <c>AddReverseProxy</c> registers Data Protection for its session-affinity cookie,
/// but that cookie carries a destination ID, not a secret, so the public proxy has no reason to read or write the vault's key
/// ring. An <see cref="EphemeralDataProtectionProvider"/> (one random key in memory) is enough, and keeps the public app's
/// Data Protection out of <c>vault.db</c> and off the disk entirely.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two registrations, not one.</b> The provider replacement alone is not enough: <c>AddDataProtection</c> also registers
/// <c>DataProtectionHostedService</c>, which eagerly resolves the key-ring provider at startup and would create a key in the
/// default file-system repository. <see cref="RemoveKeyRingWarmup"/> removes that one hosted service (by its framework type
/// name) after YARP has added its services, so nothing ever asks for a key ring in this container.
/// </para>
/// <para>
/// The admin app keeps the vault-backed ring (<c>AddPaduraksaDataProtection</c>): the dashboard's antiforgery and circuit
/// state must survive a restart, and the admin session cookie itself is Paduraksa's own random value, not Data Protection.
/// </para>
/// </remarks>
internal static class EphemeralDataProtection
{
    /// <summary>The framework's key-ring warm-up hosted service, whose full type name is stable across .NET 8-10.</summary>
    internal const string HostedServiceTypeName = "Microsoft.AspNetCore.DataProtection.Internal.DataProtectionHostedService";

    /// <summary>Registers the in-memory provider before Data Protection's own <c>TryAdd</c> from YARP can take effect.</summary>
    public static void Add(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
    }

    /// <summary>Removes the key-ring warm-up hosted service that <c>AddDataProtection</c> added, so no key is ever created.</summary>
    public static void RemoveKeyRingWarmup(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        for (int i = services.Count - 1; i >= 0; i--)
        {
            ServiceDescriptor descriptor = services[i];
            if (descriptor.ServiceType == typeof(IHostedService)
                && string.Equals(descriptor.ImplementationType?.FullName, HostedServiceTypeName, StringComparison.Ordinal))
            {
                services.RemoveAt(i);
            }
        }
    }
}
