using System.Net;
using System.Net.Sockets;

using Microsoft.AspNetCore.Connections;


namespace Paduraksa.Tests.Proxy.Tls;

/// <summary>Starting a QUIC test server on a port that is free for both TCP and UDP.</summary>
internal static class QuicTestSupport
{
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
                return await start(QuicSupport.FreeTcpAndUdpPort(address));
            }
            catch (Exception error) when (attempt < Attempts && IsAddressInUse(error))
            {
                // Lost the race for the port: probe again.
            }
        }
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
