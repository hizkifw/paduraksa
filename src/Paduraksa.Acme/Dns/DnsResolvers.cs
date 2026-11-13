using System.Net;
using System.Net.NetworkInformation;

namespace Paduraksa.Acme.Dns;

/// <summary>The recursive resolvers the DNS-01 helpers ask (the propagation check and CNAME delegation): the configured ones, else the system's.</summary>
internal static class DnsResolvers
{
    public static IReadOnlyList<IPEndPoint> For(DnsPropagationOptions options)
    {
        if (options.Resolvers.Count > 0)
        {
            return options.Resolvers;
        }

        List<IPEndPoint> system = [];
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                foreach (var address in network.GetIPProperties().DnsAddresses)
                {
                    // fec0::/10 site-local addresses are a Windows placeholder for "no IPv6 resolver", not a server.
                    if (!address.IsIPv6SiteLocal && !system.Any(e => e.Address.Equals(address)))
                    {
                        system.Add(new IPEndPoint(address, 53));
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // No list of interfaces: the fallback below.
        }

        return system.Count > 0 ? system : options.FallbackResolvers;
    }
}
