using System.Globalization;
using System.Net;
using System.Net.Sockets;

using Microsoft.AspNetCore.Connections;

using Paduraksa.Server.Hosting;

namespace Paduraksa.Tests.Integration;

/// <summary>
/// Ports for tests that need a fixed number (0 means "off" for the admin loopback listener, and a Pebble validation port must be
/// known before the process starts). Probes are never reserved, so a start retries with the same number when another test's host
/// wins the race between the probe and Kestrel's bind, and disposes the half-started host each time.
/// </summary>
internal static class TestPorts
{
    /// <summary>A port free on IPv4 loopback and, where there is IPv6, on ::1 too: a loopback listener binds both.</summary>
    public static int FreePort()
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            using TcpListener v4 = new(IPAddress.Loopback, 0);
            v4.Start();
            int port = ((IPEndPoint)v4.LocalEndpoint).Port;
            if (!Socket.OSSupportsIPv6)
            {
                return port;
            }

            try
            {
                using TcpListener v6 = new(IPAddress.IPv6Loopback, port);
                v6.Start();
                return port;
            }
            catch (SocketException)
            {
                // Taken on ::1: try another.
            }
        }

        throw new InvalidOperationException("No port is free on both loopback addresses.");
    }

    /// <summary>
    /// A loopback port nothing will ever answer on, held for as long as the returned scope lives: the socket is bound but never
    /// listening, so a connect to it is refused (Linux sends an RST). Use it for an upstream that must fail, where
    /// <see cref="FreePort"/> would only be free at the moment it was probed.
    /// </summary>
    public static DeadPort ReserveDeadPort() => new();

    /// <summary>A bound, never-listening loopback socket: as long as it lives, nothing can answer on its port.</summary>
    public sealed class DeadPort : IDisposable
    {
        private readonly Socket _socket;
        private int _disposed;

        internal DeadPort()
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            // Deliberately no Listen(): a connect is refused rather than queued.
            Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        }

        /// <summary>The port, which nothing listens on.</summary>
        public int Port { get; }

        /// <summary><c>http://127.0.0.1:{Port}/</c>: a destination address a forward can only fail against.</summary>
        public string Address => string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{Port}/");

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _socket.Dispose();
            }
        }
    }

    /// <summary>The bind lost a race for the port (Kestrel wraps the socket error).</summary>
    public static bool IsAddressInUse(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is AddressInUseException or SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Starts a host on a freshly probed port, retrying the whole start (with a new port) when that port was taken.</summary>
    public static async Task<(PaduraksaHost Host, int Port)> StartOnFreePortAsync(Func<int, PaduraksaHost> create, int attempts = 5)
    {
        ArgumentNullException.ThrowIfNull(create);
        for (int attempt = 1; ; attempt++)
        {
            int port = FreePort();
            PaduraksaHost host = create(port);
            if (await TryStartAsync(host, attempt, attempts).ConfigureAwait(false))
            {
                return (host, port);
            }
        }
    }

    /// <summary>Starts a host on <paramref name="port"/>, retrying the bind when another host takes it in the gap.</summary>
    public static async Task<PaduraksaHost> StartOnPortAsync(int port, Func<PaduraksaHost> create, int attempts = 5)
    {
        ArgumentNullException.ThrowIfNull(create);
        for (int attempt = 1; ; attempt++)
        {
            PaduraksaHost host = create();
            if (await TryStartAsync(host, attempt, attempts).ConfigureAwait(false))
            {
                return host;
            }
        }
    }

    private static async Task<bool> TryStartAsync(PaduraksaHost host, int attempt, int attempts)
    {
        try
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            return true;
        }
        catch (Exception ex) when (attempt < attempts && IsAddressInUse(ex))
        {
            await host.DisposeAsync();
            return false;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }
}
