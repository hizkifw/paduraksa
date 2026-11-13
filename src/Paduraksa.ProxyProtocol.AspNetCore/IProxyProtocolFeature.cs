using System.Net;

namespace Paduraksa.ProxyProtocol.AspNetCore;

/// <summary>
/// Connection feature set by <see cref="ProxyProtocolListenOptionsExtensions.UseProxyProtocol"/> when a connection began with a
/// PROXY protocol header. It is absent on a connection that sent none (<see cref="ProxyProtocolOptions.Required"/> is false).
/// </summary>
public interface IProxyProtocolFeature
{
    /// <summary>The header the proxy sent.</summary>
    ProxyProtocolHeader Header { get; }

    /// <summary>The socket's remote endpoint: the proxy itself. When the header carries addresses, the connection's own
    /// <c>RemoteEndPoint</c> is the client's instead.</summary>
    EndPoint? ProxyEndPoint { get; }
}
