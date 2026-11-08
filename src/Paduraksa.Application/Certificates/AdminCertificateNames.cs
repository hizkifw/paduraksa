using System.Net;
using System.Net.Sockets;

using Paduraksa.Core.Vault;

namespace Paduraksa.Application.Certificates;

/// <summary>
/// The names the admin certificate must cover (PLAN §3, §10.1): the configured ones plus everything the owner can reach
/// the admin UI by, so the wizard's "trust this root once" is enough whatever name or address they use.
/// </summary>
public static class AdminCertificateNames
{
    /// <summary>
    /// The most names one certificate may carry: the vault's own limit (<see cref="CertificateLimits.MaxNames"/>), so the
    /// configured list is capped below it (see <see cref="MaxConfiguredNames"/>).
    /// </summary>
    public const int MaxNames = CertificateLimits.MaxNames;

    /// <summary>The names the process always adds: <c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>, the machine's host name and a specific bind address.</summary>
    public const int AutomaticNames = 5;

    /// <summary>The most names <c>Paduraksa:Admin:CertificateNames</c> may list, leaving room for the automatic ones.</summary>
    public const int MaxConfiguredNames = MaxNames - AutomaticNames;

    /// <summary>
    /// The desired names: <paramref name="configured"/> (already the operator's choice), <c>localhost</c>,
    /// <c>127.0.0.1</c>, <c>::1</c>, the machine's host name, and <paramref name="adminBind"/> when it is a specific
    /// address (a wildcard bind names no host, and an IPv6 zone/scope id is dropped: it is not a certificate name).
    /// Normalised like any certificate's SANs, so an unusable entry is dropped and duplicates collapse; the validator
    /// refuses an unusable configured entry at startup.
    /// </summary>
    public static IReadOnlyList<string> Compose(IEnumerable<string> configured, string? machineName, string? adminBind)
    {
        ArgumentNullException.ThrowIfNull(configured);
        List<string> names = [.. configured, "localhost", "127.0.0.1", "::1"];
        if (!string.IsNullOrWhiteSpace(machineName))
        {
            names.Add(machineName);
        }

        if (adminBind is not null
            && IPAddress.TryParse(adminBind, out IPAddress? bind)
            && !bind.Equals(IPAddress.Any)
            && !bind.Equals(IPAddress.IPv6Any))
        {
            // A scope id (fe80::1%eth0) belongs to the interface, not to the certificate (and reading it on an IPv4
            // address throws).
            names.Add(bind.AddressFamily == AddressFamily.InterNetworkV6 && bind.ScopeId != 0
                ? new IPAddress(bind.GetAddressBytes()).ToString()
                : bind.ToString());
        }

        return CertificateNames.Normalize(names, out _);
    }

    /// <summary>
    /// The <c>https://...</c> URLs the admin UI answers on, one per concrete name (a wildcard names no URL), for the startup
    /// log so a <c>docker logs</c> reader can see where to go. IPv6 addresses are bracketed.
    /// </summary>
    public static IReadOnlyList<string> Urls(IReadOnlyList<string> names, int port)
    {
        ArgumentNullException.ThrowIfNull(names);
        List<string> urls = [];
        foreach (string name in names)
        {
            if (name.StartsWith("*.", StringComparison.Ordinal))
            {
                continue;
            }

            string host = name.Contains(':', StringComparison.Ordinal) ? $"[{name}]" : name;
            urls.Add($"https://{host}:{port}/");
        }

        return urls;
    }
}
