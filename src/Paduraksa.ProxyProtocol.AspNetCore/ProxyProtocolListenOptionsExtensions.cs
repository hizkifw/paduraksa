using System.Net.Sockets;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Paduraksa.ProxyProtocol.AspNetCore;

/// <summary>Adds PROXY protocol support to a Kestrel TCP listener.</summary>
public static class ProxyProtocolListenOptionsExtensions
{
    private static readonly Action<ILogger, string, string, Exception?> RefusesEveryConnection = LoggerMessage.Define<string, string>(
        LogLevel.Warning,
        new EventId(2, nameof(RefusesEveryConnection)),
        "UseProxyProtocol on {EndPoint} refuses every connection: {Reason}");

    private static readonly Action<ILogger, string, string, Exception?> HonoursNoHeader = LoggerMessage.Define<string, string>(
        LogLevel.Warning,
        new EventId(3, nameof(HonoursNoHeader)),
        "UseProxyProtocol on {EndPoint} honours no PROXY protocol header: {Reason}");

    /// <summary>
    /// Reads a PROXY protocol header (v1 or v2) from the start of every connection, then sets the connection's remote and local
    /// endpoints (so <c>HttpContext.Connection.RemoteIpAddress</c> is the client's) and the <see cref="IProxyProtocolFeature"/>.
    /// <b>Call it before <c>UseHttps</c></b>: the proxy's header precedes the TLS handshake and must be consumed from the raw stream. Kestrel doesn't
    /// let this method see whether <c>UseHttps</c> already ran, so the wrong order isn't detected: every HTTPS connection through the load balancer is then
    /// closed during the TLS handshake, which looks like a refusal.
    /// </summary>
    /// <remarks>
    /// The default is secure: <see cref="ProxyProtocolOptions.TrustedProxies"/> is empty, so until you add your load balancers every
    /// connection is refused, and a warning says so once, here. A connection is aborted without a log line of its own; count or log them with
    /// <see cref="ProxyProtocolOptions.OnOutcome"/>.
    /// Only a trusted proxy's connections are honoured, but anything that can connect from a trusted address can claim any client address:
    /// keep the listener reachable by the load balancer alone (firewall, private network).
    /// </remarks>
    /// <param name="listenOptions">The TCP listener.</param>
    /// <param name="configure">Sets <see cref="ProxyProtocolOptions"/>; read once, here.</param>
    /// <returns><paramref name="listenOptions"/>, for chaining.</returns>
    public static ListenOptions UseProxyProtocol(this ListenOptions listenOptions, Action<ProxyProtocolOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(listenOptions);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ProxyProtocolOptions();
        configure(options);
        var logger = listenOptions.ApplicationServices.GetService<ILoggerFactory>()?.CreateLogger("Paduraksa.ProxyProtocol.AspNetCore");
        if (logger is not null && NothingTrusted(listenOptions, options) is { } reason)
        {
            (options.Required ? RefusesEveryConnection : HonoursNoHeader)(logger, listenOptions.EndPoint.ToString() ?? string.Empty, reason, null);
        }

        listenOptions.Use(next => new ProxyProtocolMiddleware(next, options, logger).OnConnectionAsync);
        return listenOptions;
    }

    // The first-run mistake: the secure default looks like a broken listener, and nothing else says why.
    private static string? NothingTrusted(ListenOptions listenOptions, ProxyProtocolOptions options)
    {
        if (listenOptions.EndPoint is UnixDomainSocketEndPoint)
        {
            return options.TrustUnixSockets ? null : "it is a Unix socket and ProxyProtocolOptions.TrustUnixSockets is false.";
        }

        return options.TrustedProxies.Count == 0 ? "ProxyProtocolOptions.TrustedProxies is empty." : null;
    }
}
