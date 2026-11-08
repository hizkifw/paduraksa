using System.Net;
using System.Net.Sockets;

namespace Paduraksa.Admin.Auth;

/// <summary>
/// Which client addresses may connect to the admin listener (<c>Paduraksa:Listen:AdminAllowedSources</c>). Empty allows everyone;
/// otherwise a client must be inside one of the blocks. <b>Loopback is not implied</b>: a proxy on this machine that forwards a public
/// host to the admin port would make every client look like 127.0.0.1, so loopback must be listed (<c>127.0.0.1/32</c>, <c>::1/128</c>) to be
/// allowed. The server applies the policy per connection, before TLS: a refused client's TCP handshake completes, then the connection
/// is aborted without a byte sent.
/// </summary>
public sealed class AdminSourcePolicy
{
    private readonly IPNetwork[] _networks;

    /// <summary>Builds the policy from addresses and CIDR blocks.</summary>
    /// <exception cref="FormatException">An entry is neither an address nor a CIDR block.</exception>
    public AdminSourcePolicy(IEnumerable<string> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _networks = [.. sources.Select(s => TryParseBlock(s, out IPNetwork network) ? network : throw new FormatException($"'{s}' is not an IP address or CIDR block."))];
    }

    /// <summary>Whether any restriction is configured.</summary>
    public bool IsRestricted => _networks.Length > 0;

    /// <summary>
    /// Parses an address (a /32 or /128 block) or a CIDR block; false for anything else. An IPv4-mapped IPv6 block (<c>::ffff:a.b.c.d/n</c>)
    /// is refused: write the IPv4 block instead (clients are matched as IPv4 whichever way they connect).
    /// </summary>
    public static bool TryParseBlock(string? text, out IPNetwork network)
    {
        network = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (trimmed.Contains('/', StringComparison.Ordinal))
        {
            return IPNetwork.TryParse(trimmed, out network) && !network.BaseAddress.IsIPv4MappedToIPv6;
        }

        if (!IPAddress.TryParse(trimmed, out IPAddress? address))
        {
            return false;
        }

        address = Normalize(address);
        network = new IPNetwork(address, address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
        return true;
    }

    /// <summary>Whether a client at <paramref name="remote"/> may connect. An unknown address is refused once a restriction exists.</summary>
    public bool IsAllowed(IPAddress? remote)
    {
        if (!IsRestricted)
        {
            return true;
        }

        if (remote is null)
        {
            return false;
        }

        remote = Normalize(remote);
        foreach (IPNetwork network in _networks)
        {
            if (network.Contains(remote))
            {
                return true;
            }
        }

        return false;
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
