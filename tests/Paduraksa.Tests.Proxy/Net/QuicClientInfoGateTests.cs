using System.Net;
using Microsoft.AspNetCore.Connections;
using Paduraksa.Proxy.Net;
using Paduraksa.ProxyProtocol;

namespace Paduraksa.Tests.Proxy.Net;

/// <summary>Stage A on QUIC: the handshake gate attaches the same ClientInfo the TCP middleware would.</summary>
public sealed class QuicClientInfoGateTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    public void TheQuicGateAttachesTheSameClientInfoAsTcp(string remote)
    {
        DefaultConnectionContext connection = new();
        IPEndPoint endPoint = new(IPAddress.Parse(remote), 4433);

        Assert.True(QuicClientInfoGate.Gate(endPoint, connection));

        var info = connection.Features.Get<IClientInfoFeature>()!.ClientInfo;
        Assert.Equal(IpKey.Parse("127.0.0.1"), info.Address);
        Assert.Equal(4433, info.Port);
        Assert.Equal(ConnectionAddressSource.Socket, info.Source);
        Assert.True(QuicClientInfoGate.Gate(null, null)); // no endpoint, no connection: admitted, nothing to attach
    }
}
