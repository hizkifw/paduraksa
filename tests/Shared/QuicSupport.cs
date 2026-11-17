// Linked into Paduraksa.Tests.Proxy and Paduraksa.Tests.Integration (<Compile Include Link>), so it compiles into each assembly as an
// internal type of its own.
using System.Globalization;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;

namespace Paduraksa.Tests.Shared;

/// <summary>
/// QUIC needs libmsquic, which a dev machine usually lacks: there the H3 tests are skipped. CI installs it and sets <c>PADURAKSA_REQUIRE=quic</c>
/// (<see cref="Requirements"/>), which turns a missing QUIC stack into a failure.
/// </summary>
internal static class QuicSupport
{
    public static void Require()
    {
        if (QuicListener.IsSupported && QuicConnection.IsSupported)
        {
            return;
        }

        Requirements.SkipOrFail(Requirements.Quic, "QUIC isn't supported here (libmsquic missing). tests/Paduraksa.Tests.Proxy/Tls/run-quic-tests.sh runs these in Docker.");
    }

    /// <summary>
    /// A port currently free for both TCP and UDP (TCP and QUIC share it). Null means "any", probed the way Kestrel's
    /// <c>ListenAnyIP</c> binds: dual-mode <c>[::]</c>, or <c>0.0.0.0</c> without IPv6.
    /// </summary>
    public static int FreeTcpAndUdpPort(IPAddress? address = null)
    {
        var bind = address ?? (Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any);
        var dualMode = address is null && bind.Equals(IPAddress.IPv6Any);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var tcp = new Socket(bind.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            using var udp = new Socket(bind.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            if (dualMode)
            {
                tcp.DualMode = true;
                udp.DualMode = true;
            }

            tcp.Bind(new IPEndPoint(bind, 0));
            var port = ((IPEndPoint)tcp.LocalEndPoint!).Port;
            try
            {
                udp.Bind(new IPEndPoint(bind, port));
                return port;
            }
            catch (SocketException)
            {
                // Taken on UDP: try another.
            }
        }

        throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"No port free for both TCP and UDP on {bind}."));
    }
}
