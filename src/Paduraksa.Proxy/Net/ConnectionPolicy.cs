using Paduraksa.Core.Model;

namespace Paduraksa.Proxy.Net;

/// <summary>
/// What the connection stage applies, compiled once per snapshot: the per-IP cap and the connection deny list (a named
/// access list's <c>deny</c> addresses, the same <see cref="CidrSet"/> the host chains use). PLAN §7.2 A.
/// </summary>
public sealed class ConnectionPolicy
{
    /// <summary>Nothing applies: a snapshot that wasn't compiled from a config (the bootstrap one).</summary>
    public static ConnectionPolicy Off { get; } = new(0, CidrSet.Empty, null, null);

    internal ConnectionPolicy(int maxPerIp, CidrSet deny, AccessListId? denyListId, string? denyListName)
    {
        MaxPerIp = maxPerIp;
        Deny = deny;
        DenyListId = denyListId;
        DenyListName = denyListName;
    }

    /// <summary>The most open connections one client address may hold (an IPv6 /64 counts as one address); 0 is off. Trusted
    /// proxies are exempt, because every visitor behind one shares its address.</summary>
    public int MaxPerIp { get; }

    /// <summary>The addresses refused before TLS, trusted proxies included: empty when no list is named.</summary>
    public CidrSet Deny { get; }

    /// <summary>The access list the deny addresses came from, for the simulator and the dashboard.</summary>
    public AccessListId? DenyListId { get; }

    public string? DenyListName { get; }

    /// <summary>The connection stage has nothing to do, so the middleware passes a connection straight on.</summary>
    public bool IsInert => MaxPerIp == 0 && Deny.IsEmpty;
}
