using Paduraksa.Core.Model;

namespace Paduraksa.Acme.Dns;

/// <summary>The API hosts a DNS provider's credential may be sent to, which the vault enforces when the secret is stored.</summary>
public static class DnsProviderHosts
{
    /// <summary>The one host <paramref name="kind"/>'s provider talks to.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The kind is undefined.</exception>
    public static string ApiHostOf(DnsProviderKind kind) => kind switch
    {
        DnsProviderKind.Cloudflare => CloudflareDnsProvider.ApiHost,
        DnsProviderKind.DeSec => DesecDnsProvider.ApiHost,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Undefined DNS provider kind."),
    };
}
