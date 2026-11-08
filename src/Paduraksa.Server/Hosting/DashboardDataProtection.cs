using System.Xml.Linq;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// The admin app's ASP.NET Data Protection (antiforgery tokens, the dashboard's circuits and persisted component state). PLAN §10.1 puts
/// its key ring in the vault; until that lands this is the one place that registers it, with keys that live in memory only.
/// </summary>
internal static class DashboardDataProtection
{
    /// <summary>The application name the key ring is isolated under (stable, so a persisted ring stays usable across paths and versions).</summary>
    public const string ApplicationName = "Paduraksa.Admin";

    /// <summary>
    /// TODO(M5): vault key ring. Replace the body with the vault-held key ring (<c>AddPaduraksaDataProtection(services, keyStore)</c> from
    /// the container-and-keyring work) once it merges; the call site in <see cref="AdminApp"/> stays as it is.
    /// </summary>
    /// <remarks>
    /// Until then the keys are created in memory and never written anywhere: not to disk (the framework's default on Linux is
    /// <c>~/.aspnet/DataProtection-Keys</c>, in plain text), not to the database. The cost is that a restart invalidates what they protect:
    /// open pages reconnect with a fresh page load, and a sign-in form rendered before the restart has to be submitted again. Sessions
    /// are not affected (the session cookie is a random value checked against the database, not a protected payload).
    /// </remarks>
    public static IServiceCollection AddDashboardDataProtection(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // The ephemeral provider keeps its keys in memory and never builds the key manager.
        services.AddDataProtection().SetApplicationName(ApplicationName).UseEphemeralDataProtectionProvider();

        // The framework's hosted service would still build the (unused) key manager at startup and create a key in its default
        // repository, a file, warning that it is unencrypted. Nothing here uses that key ring, so the service goes; and should anything
        // build the key manager anyway, its repository is this process's memory.
        ServiceDescriptor[] keyRingLoaders =
        [
            .. services.Where(static d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.FullName == HostedServiceTypeName),
        ];
        foreach (ServiceDescriptor loader in keyRingLoaders)
        {
            services.Remove(loader);
        }

        services.Configure<KeyManagementOptions>(static options => options.XmlRepository = new InMemoryKeyRepository());
        return services;
    }

    private const string HostedServiceTypeName = "Microsoft.AspNetCore.DataProtection.Internal.DataProtectionHostedService";

    /// <summary>A key repository that keeps the key ring's XML in memory for the life of the process.</summary>
    private sealed class InMemoryKeyRepository : IXmlRepository
    {
        private readonly Lock _gate = new();
        private readonly List<XElement> _elements = [];

        public IReadOnlyCollection<XElement> GetAllElements()
        {
            lock (_gate)
            {
                return [.. _elements.Select(static element => new XElement(element))];
            }
        }

        public void StoreElement(XElement element, string friendlyName)
        {
            ArgumentNullException.ThrowIfNull(element);
            lock (_gate)
            {
                _elements.Add(new XElement(element));
            }
        }
    }
}
