using System.Net;
using System.Net.Quic;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;

using Paduraksa.Proxy.Net;

namespace Paduraksa.Proxy.Tls;

/// <summary>Where and how the public HTTPS endpoint listens.</summary>
public sealed class PublicHttpsListenerOptions
{
    /// <summary>
    /// Bind address. Null (the default) means any address: dual-mode <c>[::]</c>, falling back to <c>0.0.0.0</c> on
    /// hosts without IPv6 (Kestrel's <c>ListenAnyIP</c>).
    /// </summary>
    public IPAddress? Address { get; init; }

    /// <summary>The bound port, shared by TCP and UDP. 0 (OS-assigned) only works when no H3 listener is added.</summary>
    public int Port { get; init; } = 443;

    /// <summary>Global H3 switch. The UDP listener is added only if this is set and libmsquic is available.</summary>
    public bool EnableHttp3 { get; init; } = true;

    /// <summary>Adds TCP connection middleware (PROXY protocol, IP gate, per-IP cap, ClientInfo). It runs before TLS,
    /// so it is called before <c>UseHttps</c>.</summary>
    public Action<ListenOptions>? ConfigureTcpConnection { get; init; }

    /// <summary>Adds QUIC connection middleware, which runs for each established connection (not in the handshake).</summary>
    public Action<ListenOptions>? ConfigureQuicConnection { get; init; }
}

/// <summary>Kestrel listener layout for the public app: per HTTPS endpoint, two <c>Listen</c> calls on the same
/// address and port, TCP (H1/H2) and UDP (H3), each with its own handshake callback.</summary>
public static class PublicListeners
{
    /// <summary>Whether this process can serve HTTP/3 (libmsquic present). <c>paduraksa doctor</c> reports it.</summary>
    public static bool IsHttp3Supported => QuicListener.IsSupported;

    /// <summary>Registers what per-SNI HTTPS on QUIC needs on the empty or slim builder (<c>UseKestrelCore()</c>):
    /// <c>UseKestrelHttpsConfiguration()</c>, without which an H3 listener using <c>UseHttps(callback)</c> fails at
    /// startup, and <c>UseQuic()</c>, the QUIC transport.</summary>
    public static IWebHostBuilder UsePublicHttpsTransports(this IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseKestrelHttpsConfiguration().UseQuic();
    }

    /// <summary>
    /// Adds the public HTTPS listeners: TCP <c>Http1AndHttp2</c> (connection middleware, then the TCP handshake
    /// callback) and, when H3 is enabled and supported, UDP <c>Http3</c> on the same endpoint (the QUIC callback,
    /// which also runs the connection gate). Kestrel's own <c>Alt-Svc</c> is disabled on both: it advertises the bound
    /// port, so Paduraksa emits the header itself (<see cref="AltSvcHeader"/>).
    /// </summary>
    /// <returns>True if the H3 listener was added (pass it to <see cref="AltSvcHeader"/>).</returns>
    /// <exception cref="ArgumentException">Port 0 with an H3 listener (TCP and UDP would get different ports).</exception>
    public static bool ListenPublicHttps(this KestrelServerOptions kestrel, PublicHttpsListenerOptions options, TlsHandshakeSelector selector)
    {
        ArgumentNullException.ThrowIfNull(kestrel);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentOutOfRangeException.ThrowIfNegative(options.Port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Port, IPEndPoint.MaxPort);

        var http3 = options.EnableHttp3 && IsHttp3Supported;
        if (http3 && options.Port == 0)
        {
            throw new ArgumentException("The public HTTPS port can't be 0 with HTTP/3 enabled: TCP and UDP must share one port.", nameof(options));
        }

        Listen(kestrel, options.Address, options.Port, listen =>
        {
            listen.Protocols = HttpProtocols.Http1AndHttp2;
            listen.DisableAltSvcHeader = true;
            options.ConfigureTcpConnection?.Invoke(listen);
            listen.UseTimedHttps(selector.CreateTcpCallbackOptions());
        });

        if (http3)
        {
            Listen(kestrel, options.Address, options.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http3;
                // Kestrel would otherwise add Alt-Svc (bound port) to H3 responses too.
                listen.DisableAltSvcHeader = true;
                listen.UseQuicHandshakeTiming();
                options.ConfigureQuicConnection?.Invoke(listen);
                listen.UseHttps(selector.CreateQuicCallbackOptions());
            });
        }

        return http3;
    }

    /// <summary>
    /// Adds the public plain-HTTP listener (H1: ACME HTTP-01, redirects, plain-HTTP hosts). Null address = any
    /// (dual-mode <c>[::]</c>). <paramref name="configure"/> can add connection middleware (PROXY protocol, IP gate).
    /// </summary>
    public static void ListenPublicHttp(this KestrelServerOptions kestrel, IPAddress? address, int port, Action<ListenOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(kestrel);
        Listen(kestrel, address, port, listen =>
        {
            listen.Protocols = HttpProtocols.Http1;
            configure?.Invoke(listen);
            listen.UseConnectionTimings();
        });
    }

    private static void Listen(KestrelServerOptions kestrel, IPAddress? address, int port, Action<ListenOptions> configure)
    {
        if (address is null)
        {
            kestrel.ListenAnyIP(port, configure);
        }
        else
        {
            kestrel.Listen(address, port, configure);
        }
    }
}
