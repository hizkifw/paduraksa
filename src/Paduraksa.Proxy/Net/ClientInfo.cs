using System.Net;

namespace Paduraksa.Proxy.Net;

/// <summary>Where <see cref="ClientInfo.Address"/> came from.</summary>
public enum ConnectionAddressSource : byte
{
    /// <summary>The socket's remote endpoint (no PROXY protocol on this listener).</summary>
    Socket = 0,

    /// <summary>A PROXY protocol header from a trusted source carried the client address.</summary>
    ProxyProtocol = 1,

    /// <summary>A PROXY protocol header was required and received, but carried no address (v2 <c>LOCAL</c>,
    /// <c>AF_UNSPEC</c>/<c>AF_UNIX</c>, or v1 <c>UNKNOWN</c>, e.g. a load balancer's own health check), so the
    /// socket's remote endpoint is used, as the spec requires.</summary>
    ProxyProtocolUnspecified = 2,
}

/// <summary>
/// The connection-level view of the client, computed once per connection in stage A and exposed as
/// a connection feature (<see cref="IClientInfoFeature"/>).
/// </summary>
/// <remarks>
/// <b>Immutable by design.</b> On HTTP/2 and HTTP/3 every stream's feature collection falls back to the same
/// connection features, so concurrent requests share this instance. Anything per request (such as a
/// header-derived client IP) belongs in per-request state, never here. Store it with
/// <c>ConnectionContext.Features.Set&lt;IClientInfoFeature&gt;(info)</c>; never use
/// <c>IConnectionItemsFeature.Items</c>, which is one non-thread-safe dictionary shared by all streams.
/// </remarks>
public sealed class ClientInfo : IClientInfoFeature
{
    public ClientInfo(
        IpKey address,
        ushort port,
        ConnectionAddressSource source,
        IpKey transportAddress,
        ushort transportPort,
        ushort countryIndex = 0)
    {
        Address = address;
        Port = port;
        Source = source;
        TransportAddress = transportAddress;
        TransportPort = transportPort;
        CountryIndex = countryIndex;
    }

    /// <summary>The client address at connection level: the PROXY protocol source address if one was received,
    /// otherwise the socket peer.</summary>
    public IpKey Address { get; }

    /// <summary>The client port matching <see cref="Address"/> (0 if unknown).</summary>
    public ushort Port { get; }

    public ConnectionAddressSource Source { get; }

    /// <summary>The socket's remote address: the load balancer when PROXY protocol was used, otherwise the same as
    /// <see cref="Address"/>.</summary>
    public IpKey TransportAddress { get; }

    public ushort TransportPort { get; }

    /// <summary>Index into the snapshot's country table; 0 = unknown. Placeholder until the geo lookup exists.</summary>
    public ushort CountryIndex { get; }

    /// <summary>True when <see cref="Address"/> came from a PROXY protocol header.</summary>
    public bool IsFromProxyProtocol => Source == ConnectionAddressSource.ProxyProtocol;

    ClientInfo IClientInfoFeature.ClientInfo => this;

    /// <summary>From the socket peer only (no PROXY protocol), e.g. the QUIC TLS callback's
    /// <c>Connection.RemoteEndPoint</c>. IPv4-mapped peers normalise to IPv4 keys.</summary>
    public static ClientInfo FromEndPoint(EndPoint? remoteEndPoint, ushort countryIndex = 0)
    {
        _ = IpKey.TryFromEndPoint(remoteEndPoint, out var address, out var port);
        return new ClientInfo(address, port, ConnectionAddressSource.Socket, address, port, countryIndex);
    }

    /// <summary>A copy with the country index set (for a later geo stage).</summary>
    public ClientInfo WithCountryIndex(ushort countryIndex) =>
        new(Address, Port, Source, TransportAddress, TransportPort, countryIndex);

    public override string ToString() =>
        Source == ConnectionAddressSource.ProxyProtocol
            ? $"{Address}:{Port} via {TransportAddress}:{TransportPort} (PROXY protocol)"
            : $"{Address}:{Port}";
}

/// <summary>Connection feature carrying the immutable <see cref="Net.ClientInfo"/>. Read it per request with
/// <c>HttpContext.Features.Get&lt;IClientInfoFeature&gt;()</c> (0 B on HTTP/1.1 and HTTP/2).</summary>
public interface IClientInfoFeature
{
    ClientInfo ClientInfo { get; }
}
