using System.Globalization;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;

using Microsoft.AspNetCore.Connections;

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

    /// <summary>
    /// Starts a server on a port free for both TCP and UDP, retrying with a new port when the bind loses a race
    /// (another process took the port between the probe and Kestrel's bind).
    /// </summary>
    /// <param name="address">The bind address as passed to the listener; null = any, probed like Kestrel binds it.</param>
    /// <param name="start">Starts the server on the given port.</param>
    public static async Task<TlsTestServer> StartOnFreePortAsync(IPAddress? address, Func<int, Task<TlsTestServer>> start)
    {
        ArgumentNullException.ThrowIfNull(start);
        const int Attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await start(FreeTcpAndUdpPort(address));
            }
            catch (Exception error) when (attempt < Attempts && IsAddressInUse(error))
            {
                // Lost the race for the port: probe again.
            }
        }
    }

    /// <summary>
    /// A port currently free for both TCP and UDP (TCP and QUIC share it). Null means "any", probed the way Kestrel's
    /// <c>ListenAnyIP</c> binds: dual-mode <c>[::]</c>, or <c>0.0.0.0</c> without IPv6.
    /// </summary>
    public static int FreeTcpAndUdpPort(IPAddress? address)
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

    private static bool IsAddressInUse(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            // Kestrel's socket transport throws AddressInUseException; a UDP (QUIC) bind conflict surfaces as a
            // SocketException with AddressAlreadyInUse.
            if (current is AddressInUseException or SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
            {
                return true;
            }
        }

        return false;
    }
}
