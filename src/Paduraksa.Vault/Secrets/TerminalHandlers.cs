using System.Net;
using System.Security.Authentication;
using Paduraksa.Core.Vault;

namespace Paduraksa.Vault.Secrets;

/// <summary>Builds the terminal handler of an authenticating handler: the vault's own <see cref="SocketsHttpHandler"/>.</summary>
internal static class TerminalHandlers
{
    /// <summary>
    /// A <see cref="SocketsHttpHandler"/> that does no automatic redirects, keeps no cookies, uses no system proxy (only
    /// <see cref="SecretHttpTransportOptions.ProxyAddress"/>), sends no credentials of its own, and takes its timeouts from
    /// <paramref name="transport"/>. TLS 1.2 and 1.3 with the platform's normal certificate validation.
    /// </summary>
    public static SocketsHttpHandler CreateSockets(SecretHttpTransportOptions transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        RequirePositive(transport.ConnectTimeout, nameof(transport.ConnectTimeout));
        RequirePositive(transport.PooledConnectionLifetime, nameof(transport.PooledConnectionLifetime));
        RequirePositive(transport.PooledConnectionIdleTimeout, nameof(transport.PooledConnectionIdleTimeout));

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = transport.ProxyAddress is not null,
            Proxy = transport.ProxyAddress is null ? null : new WebProxy(transport.ProxyAddress),
            AutomaticDecompression = DecompressionMethods.None,
            PreAuthenticate = false,
            ConnectTimeout = transport.ConnectTimeout,
            PooledConnectionLifetime = transport.PooledConnectionLifetime,
            PooledConnectionIdleTimeout = transport.PooledConnectionIdleTimeout,
        };
        handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        return handler;
    }

    private static void RequirePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, value, "Must be positive.");
        }
    }
}
