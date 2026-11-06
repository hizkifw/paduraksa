using System.Globalization;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;

namespace Paduraksa.Tests.Proxy.Tls;

/// <summary>
/// QUIC tests need libmsquic, which normal dev machines and CI don't have: there they are skipped (xUnit v3 dynamic
/// skip). <c>run-quic-tests.sh</c> runs them in Docker with libmsquic and sets <see cref="RequireVariable"/>, which
/// turns a missing QUIC stack into a failure so a "green" Docker run can't be all skips.
/// </summary>
internal static class QuicTestSupport
{
    public const string RequireVariable = "PADURAKSA_REQUIRE_QUIC";

    public static void Require()
    {
        if (QuicListener.IsSupported && QuicConnection.IsSupported)
        {
            return;
        }

        if (Environment.GetEnvironmentVariable(RequireVariable) == "1")
        {
            Assert.Fail($"{RequireVariable}=1 but QUIC isn't supported here (libmsquic missing?).");
        }

        Assert.Skip("QUIC isn't supported here (libmsquic missing). Run tests/Paduraksa.Tests.Proxy/Tls/run-quic-tests.sh.");
    }

    /// <summary>A port that is currently free for both TCP and UDP on <paramref name="address"/> (TCP and QUIC share it).</summary>
    public static int FreeTcpAndUdpPort(IPAddress address)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var tcp = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            tcp.Bind(new IPEndPoint(address, 0));
            var port = ((IPEndPoint)tcp.LocalEndPoint!).Port;
            using var udp = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                udp.Bind(new IPEndPoint(address, port));
                return port;
            }
            catch (SocketException)
            {
                // Taken on UDP: try another.
            }
        }

        throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"No port free for both TCP and UDP on {address}."));
    }
}
